import { useEffect, useRef, useState } from 'react';
import { api } from '@appdeploy/client';
import type { RepairResult } from './epub';

type Device = { workspaceId: string; accessKey: string };
type Connection = {
  configured: boolean;
  connected: boolean;
  provider: string;
  maxFileBytes: number;
  chunkBytes: number;
  senderEmail: string;
  kindleEmail: string;
};
export type Job = {
  jobId: string;
  title: string;
  status: 'uploading' | 'sending' | 'accepted' | 'failed' | 'uncertain';
  nextChunk: number;
  chunks: number;
  message: string;
};
type Request = { requestId: string; jobId?: string };
function issue(e: unknown) {
  const err = e as {
    response?: { status?: number; data?: { error?: string; message?: string } };
  };
  if (err.response?.status === 429)
    return '请求过多，操作已暂停，请稍后手动重试。';
  return (
    err.response?.data?.error ||
    err.response?.data?.message ||
    (e instanceof Error && e.message) ||
    '连接暂时中断，修复版仍保留在手机，请重试或使用分享菜单。'
  );
}
async function base64(blob: Blob) {
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).split(',')[1]);
    reader.onerror = () => reject(new Error('无法读取修复版。'));
    reader.readAsDataURL(blob);
  });
}
const terminal = (job: Job) =>
  ['accepted', 'failed', 'uncertain'].includes(job.status);
export function useCloudMail(
  device: Device | null,
  senderEmail: string,
  kindleEmail: string,
) {
  const [connection, setConnection] = useState<Connection | null>(null);
  const [working, setWorking] = useState(false);
  const [sending, setSending] = useState(false);
  const [message, setMessage] = useState('');
  const [progress, setProgress] = useState(0);
  const [job, setJob] = useState<Job | null>(null);
  const blocked = useRef(false);
  const statusRunning = useRef(false);
  const sendRunning = useRef(false);
  const requests = useRef(new WeakMap<File, Request>());
  const activeFile = useRef<File | null>(null);
  const lastJob = useRef<Job | null>(null);
  const requestKey = 'qingyue.mail-requests.v1';
  function durableRequests(): Record<string, Request> {
    try {
      return JSON.parse(localStorage.getItem(requestKey) || '{}');
    } catch {
      return {};
    }
  }
  function keepRequest(id: string, request: Request) {
    const all = durableRequests();
    all[id] = request;
    try {
      localStorage.setItem(
        requestKey,
        JSON.stringify(Object.fromEntries(Object.entries(all).slice(-100))),
      );
    } catch {
      throw new Error('无法保存发送记录，请使用分享菜单，避免重复寄送。');
    }
  }
  const mounted = useRef(true);
  function received(next: Job) {
    lastJob.current = next;
    if (mounted.current) {
      setJob(next);
      setMessage(next.message);
    }
  }
  async function refresh(force = false) {
    if (!device || statusRunning.current || (!force && blocked.current)) return;
    statusRunning.current = true;
    try {
      const { data } = await api.post('/api/mail/status', device);
      if (mounted.current) setConnection(data as Connection);
    } catch (e) {
      const status = (e as { response?: { status?: number } }).response?.status;
      if (status === 429 || status === 401) blocked.current = true;
      if (mounted.current) setMessage(issue(e));
    } finally {
      statusRunning.current = false;
    }
  }
  async function connect() {
    if (!device || working || sendRunning.current) return;
    setWorking(true);
    setMessage('正在连接发件邮箱…');
    try {
      await api.post('/api/mail/connect', device);
      blocked.current = false;
      await refresh(true);
      setMessage('发件邮箱已连接，手机可独立发送，电脑无需开机。');
    } catch (e) {
      setMessage(issue(e));
    } finally {
      setWorking(false);
    }
  }
  async function disconnect() {
    if (!device || working || sendRunning.current) return;
    setWorking(true);
    try {
      await api.post('/api/mail/disconnect', device);
      setConnection(
        (previous) => previous && { ...previous, connected: false },
      );
      setMessage('手机邮件发送已断开。');
    } catch (e) {
      setMessage(issue(e));
    } finally {
      setWorking(false);
    }
  }
  async function poll(request: Request) {
    if (!device || !request.jobId) return null;
    let next: Job | null = null;
    for (let count = 0; count < 12; count++) {
      const { data } = await api.post('/api/mail/job', {
        ...device,
        jobId: request.jobId,
      });
      next = data as Job;
      received(next);
      if (terminal(next) || next.status === 'uploading') return next;
      await new Promise((resolve) => setTimeout(resolve, 3000));
    }
    if (mounted.current)
      setMessage('邮件仍在提交。请稍后点查询发送结果，避免重复发送。');
    return next;
  }
  async function send(result: RepairResult) {
    if (!device || sendRunning.current) return null;
    sendRunning.current = true;
    setSending(true);
    setProgress(0);
    setMessage('正在准备邮件…');
    if (activeFile.current !== result.file) {
      activeFile.current = result.file;
      lastJob.current = null;
      setJob(null);
    }
    let request = result.id
      ? durableRequests()[result.id]
      : requests.current.get(result.file);
    if (!request) {
      request = { requestId: crypto.randomUUID() };
      requests.current.set(result.file, request);
    }
    try {
      if (result.id) keepRequest(result.id, request);
      if (request.jobId) {
        const previous = await poll(request);
        if (previous?.status === 'failed') {
          request = { requestId: crypto.randomUUID() };
          requests.current.set(result.file, request);
          if (result.id) keepRequest(result.id, request);
        } else if (previous && previous.status !== 'uploading') return previous;
      }
      const { data: configured } = await api.post('/api/mail/status', device);
      setConnection(configured);
      if (!configured.connected) {
        setMessage('请先在设置里连接发件邮箱，再使用邮箱直推。');
        return null;
      }
      if (result.file.size > configured.maxFileBytes) {
        setMessage(
          `修复版超过此邮箱的直推上限 ${Math.floor(configured.maxFileBytes / 1024 / 1024)} MB，请保存文件后通过 Kindle 分享或 Amazon 网页发送。`,
        );
        return null;
      }
      const digest = Array.from(
        new Uint8Array(
          await crypto.subtle.digest(
            'SHA-256',
            await result.file.arrayBuffer(),
          ),
        ),
        (byte) => byte.toString(16).padStart(2, '0'),
      ).join('');
      const { data } = await api.post('/api/mail/upload', {
        ...device,
        requestId: request.requestId,
        digest,
        bytes: result.file.size,
        name: result.file.name,
        title: result.title,
        senderEmail,
        kindleEmail,
      });
      let current = data as Job;
      request.jobId = current.jobId;
      received(current);
      if (result.id) keepRequest(result.id, request);
      if (current.status !== 'uploading') return current;
      for (let index = current.nextChunk; index < current.chunks; index++) {
        const content = await base64(
          result.file.slice(
            index * configured.chunkBytes,
            (index + 1) * configured.chunkBytes,
          ),
        );
        current = (
          await api.post('/api/mail/chunk', {
            ...device,
            jobId: current.jobId,
            index,
            content,
          })
        ).data;
        setProgress(Math.round((current.nextChunk / current.chunks) * 100));
        setMessage(
          `正在上传修复版 ${Math.round((current.nextChunk / current.chunks) * 100)}%`,
        );
      }
      setMessage('正在提交到发件邮箱…');
      current = (
        await api.post('/api/mail/send', { ...device, jobId: current.jobId })
      ).data;
      received(current);
      if (!terminal(current)) return await poll(request);
      return current;
    } catch (e) {
      setMessage(issue(e));
      // A lost HTTP response must not resend an already accepted message.
      if (
        request.jobId &&
        (e as { response?: { status?: number } }).response?.status !== 429
      ) {
        try {
          const previous = await poll(request);
          if (previous?.status === 'uploading') setMessage(issue(e));
        } catch {
          /* Keep the original visible error, and allow a manual status check. */
        }
      }
      return lastJob.current;
    } finally {
      sendRunning.current = false;
      if (mounted.current) setSending(false);
    }
  }
  async function check(jobId: string) {
    if (!device || sendRunning.current) return null;
    setWorking(true);
    try {
      return await poll({ requestId: '', jobId });
    } catch (e) {
      setMessage(issue(e));
      return null;
    } finally {
      setWorking(false);
    }
  }
  function reset() {
    if (!sendRunning.current) {
      activeFile.current = null;
      setJob(null);
      setMessage('');
      setProgress(0);
    }
  }
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  useEffect(() => {
    blocked.current = false;
    setConnection(null);
    if (!device) return;
    void refresh();
    const timer = setInterval(() => {
      if (document.visibilityState === 'visible') void refresh();
    }, 30000);
    const visible = () => {
      if (document.visibilityState === 'visible') void refresh();
    };
    document.addEventListener('visibilitychange', visible);
    return () => {
      clearInterval(timer);
      document.removeEventListener('visibilitychange', visible);
    };
  }, [device, senderEmail, kindleEmail]);
  return {
    connection,
    working,
    sending,
    message,
    progress,
    job,
    connect,
    disconnect,
    send,
    check,
    reset,
    refresh: () => {
      blocked.current = false;
      void refresh(true);
    },
  };
}
