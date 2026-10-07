import { useEffect, useRef, useState } from 'react';
import { api } from '@appdeploy/client';

export type SharedSettings = { senderEmail: string; kindleEmail: string; autoSend: boolean; autoRepair: boolean; rememberHistory: boolean };
type Credential = { workspaceId: string; accessKey: string };
type State = { settings: SharedSettings; revision: string; devices: number };
const key = 'qingyue.sync-device';
const pendingKey = 'qingyue.sync-pending';
export const defaults: SharedSettings = { senderEmail: '', kindleEmail: '', autoSend: true, autoRepair: true, rememberHistory: true };
function read<T>(name: string, fallback: T): T { try { return JSON.parse(localStorage.getItem(name) || 'null') ?? fallback; } catch { return fallback; } }
function write(name: string, value: unknown) { try { localStorage.setItem(name, JSON.stringify(value)); } catch { /* Persistence can be unavailable in private browsing. */ } }
function message(e: unknown): string {
    const err = e as { response?: { data?: { error?: string; message?: string }; status?: number } };
    if (err.response?.status === 429) return '同步请求过多，已暂停自动同步，请稍后手动重试。';
    return err.response?.data?.error || err.response?.data?.message || '同步暂时不可用，修改已保留在本机。';
}
export function useSettingsSync(initial: SharedSettings, apply: (settings: SharedSettings) => void) {
    const [credential, setCredential] = useState<Credential | null>(() => read(key, null));
    const [state, setState] = useState<State | null>(null);
    const [status, setStatus] = useState('');
    const [working, setWorking] = useState(false);
    const [pairLink, setPairLink] = useState('');
    const [expiresAt, setExpiresAt] = useState(0);
    const current = useRef(state);
    const applyRef = useRef(apply);
    const initialRef = useRef(initial);
    const pending = useRef<Partial<SharedSettings>>(read(pendingKey, {}));
    const running = useRef(false);
    const blocked = useRef(false);
    const mounted = useRef(true);
    const debounce = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
    applyRef.current = apply;
    initialRef.current = initial;
    function receive(next: State) {
        current.current = next;
        if (mounted.current) setState(next);
        applyRef.current({ ...next.settings, ...pending.current });
    }
    function remember(next: Credential) { write(key, next); setCredential(next); }
    async function refresh(device = credential) {
        if (!device || running.current || blocked.current) return;
        running.current = true;
        try {
            const { data } = await api.post('/api/sync/state', device);
            receive(data as State);
            if (Object.keys(pending.current).length) {
                const outgoing = { ...pending.current };
                let saved: State;
                try { saved = (await api.post('/api/sync/save', { ...device, revision: current.current!.revision, patch: outgoing })).data; }
                catch (e) {
                    const err = e as { response?: { status?: number; data?: State } };
                    if (err.response?.status !== 409 || !err.response.data) throw e;
                    receive(err.response.data);
                    saved = (await api.post('/api/sync/save', { ...device, revision: current.current!.revision, patch: outgoing })).data;
                }
                for (const field of Object.keys(outgoing) as (keyof SharedSettings)[]) {
                    if (pending.current[field] === outgoing[field]) delete pending.current[field];
                }
                write(pendingKey, pending.current);
                receive(saved);
            }
            if (mounted.current) setStatus('设置已同步');
        } catch (e) {
            const code = (e as { response?: { status?: number } }).response?.status;
            if (code === 429 || code === 401) blocked.current = true;
            if (mounted.current) setStatus(message(e));
        } finally { running.current = false; }
    }
    function queue(changes: Partial<SharedSettings>) {
        pending.current = { ...pending.current, ...changes };
        write(pendingKey, pending.current);
        if (!credential || blocked.current) return;
        setStatus('正在同步设置…');
        clearTimeout(debounce.current);
        debounce.current = setTimeout(() => { void refresh(); }, 600);
    }
    async function pair() {
        if (working || running.current) return;
        setWorking(true); setStatus('正在生成配对信息…');
        try {
            let device = credential;
            if (!device) {
                const { data } = await api.post('/api/sync/create', { settings: { ...initialRef.current, ...pending.current } });
                device = { workspaceId: data.workspaceId, accessKey: data.accessKey };
                pending.current = {}; write(pendingKey, {});
                remember(device); receive(data as State);
            }
            const { data } = await api.post('/api/sync/pair', device);
            setPairLink(`${location.origin}${location.pathname}#pair=${encodeURIComponent(data.pairCode)}`);
            setExpiresAt(data.expiresAt);
            setStatus('配对信息已生成，5 分钟内有效');
        } catch (e) { setStatus(message(e)); }
        finally { setWorking(false); }
    }
    async function claim(value: string) {
        if (working) return;
        let pairCode = value.trim();
        try { if (pairCode.includes('#')) pairCode = new URLSearchParams(new URL(pairCode).hash.slice(1)).get('pair') || ''; }
        catch { setStatus('请粘贴完整的轻阅配对链接。'); return; }
        setWorking(true); setStatus('正在连接另一台设备…');
        try {
            const { data } = await api.post('/api/sync/claim', { pairCode });
            pending.current = {}; write(pendingKey, {});
            blocked.current = false;
            remember({ workspaceId: data.workspaceId, accessKey: data.accessKey });
            receive(data as State); setPairLink(''); setStatus('配对完成，邮箱和偏好会自动同步');
        } catch (e) { setStatus(message(e)); }
        finally { setWorking(false); }
    }
    async function disconnect() {
        if (!credential || working || running.current) return;
        setWorking(true);
        try {
            try { await api.post('/api/sync/disconnect', credential); } catch (e) { if ((e as { response?: { status?: number } }).response?.status !== 401) throw e; }
            write(key, null); write(pendingKey, {}); pending.current = {};
            setCredential(null); setState(null); current.current = null; setPairLink(''); setStatus('当前设备已断开同步');
        } catch (e) { setStatus(message(e)); }
        finally { setWorking(false); }
    }
    useEffect(() => {
        mounted.current = true;
        const fragment = new URLSearchParams(location.hash.slice(1));
        const code = fragment.get('pair');
        if (code) {
            history.replaceState(null, '', `${location.pathname}${location.search}`);
            void claim(code);
        }
        return () => { mounted.current = false; clearTimeout(debounce.current); };
    }, []);
    useEffect(() => {
        if (!credential) return;
        void refresh();
        const tick = setInterval(() => { if (document.visibilityState === 'visible') void refresh(); }, 30000);
        const foreground = () => { if (document.visibilityState === 'visible') void refresh(); };
        document.addEventListener('visibilitychange', foreground);
        return () => { clearInterval(tick); document.removeEventListener('visibilitychange', foreground); };
    }, [credential]);
    return { device: credential, flush: refresh, connected: !!credential, state, status, working, pairLink, expiresAt, queue, pair, claim, disconnect, retry: () => { blocked.current = false; void refresh(); } };
}

