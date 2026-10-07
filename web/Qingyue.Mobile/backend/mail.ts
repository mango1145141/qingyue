import { db, error, json, secrets, storage, type RouterMiddleware, type RouterRoutes } from '@appdeploy/sdk';
import nodemailer from 'nodemailer';
import { createHash } from 'node:crypto';

type Auth = { id: string; space: { settings: { senderEmail: string; kindleEmail: string } } };
type Authorize = (input: Record<string, unknown>) => Promise<Auth | null>;
type Recent = { requestId: string; jobId: string };
type Connection = { senderEmail: string; enabled: boolean; verifiedAt: number; recent: Recent[] };
type Job = {
    workspaceId: string; requestId: string; name: string; title: string;
    bytes: number; digest: string; chunks: number; nextChunk: number;
    senderEmail: string; kindleEmail: string; createdAt: number; updatedAt: number;
    expiresAt: number; status: 'uploading' | 'sending' | 'accepted' | 'failed' | 'uncertain';
    message: string; cleaned: boolean;
};
const chunkBytes = 512 * 1024;
class MailProblem extends Error { constructor(message: string, public status = 400) { super(message); } }
const object = (v: unknown): Record<string, unknown> => v && typeof v === 'object' && !Array.isArray(v) ? v as Record<string, unknown> : {};
const connectionTable = (id: string) => `mail-connection:${id}`;
const jobTable = (id: string) => `mail-job:${id}`;
const legacySecretName = (id: string) => `MAIL_${id.replace(/[^a-z0-9]/gi, '').toUpperCase()}_AUTH`;
const secretName = (auth: Auth) => `${legacySecretName(auth.id).replace(/_AUTH$/, '')}_${createHash('sha256').update(auth.space.settings.senderEmail.toLowerCase()).digest('hex').slice(0, 16).toUpperCase()}_AUTH`;
const partPath = (workspace: string, id: string, index: number) => `mail/${workspace}/${id}/part-${index}`;
function provider(email: string) {
    const domain = email.split('@')[1]?.toLowerCase();
    if (domain === '163.com') return { name: '163 邮箱', host: 'smtp.163.com', maxFileBytes: 35 * 1024 * 1024 };
    if (domain === 'qq.com' || domain === 'foxmail.com') return { name: 'QQ 邮箱', host: 'smtp.qq.com', maxFileBytes: 17 * 1024 * 1024 };
    if (domain === 'gmail.com' || domain === 'googlemail.com') return { name: 'Gmail', host: 'smtp.gmail.com', maxFileBytes: 17 * 1024 * 1024 };
    return null;
}
async function connection(id: string) {
    // This logical table contains a single connection; no growing-table scan.
    const { items } = await db.list<Connection>(connectionTable(id), { limit: 1 });
    return items[0] || null;
}
async function saveConnection(id: string, next: Connection, recordId?: string) {
    if (recordId) {
        const [ok] = await db.update(connectionTable(id), [{ id: recordId, record: { ...next } }]);
        if (!ok) throw new MailProblem('邮箱连接未能保存，请重试。', 503);
    } else {
        const [created] = await db.add(connectionTable(id), [{ ...next }]);
        if (!created) throw new MailProblem('邮箱连接未能保存，请重试。', 503);
    }
}
async function updateJob(workspace: string, id: string, next: Job) {
    const [ok] = await db.update(jobTable(workspace), [{ id, record: { ...next } }]);
    if (!ok) throw new MailProblem('发送状态暂时无法保存，请稍后查询，避免重复发送。', 503);
}
async function clean(workspace: string, id: string, job: Job) {
    if (job.cleaned) return;
    const results = await storage.delete(Array.from({ length: job.chunks }, (_, i) => partPath(workspace, id, i)));
    if (results.every(Boolean)) { job.cleaned = true; await updateJob(workspace, id, job); }
}
async function prune(workspace: string, item: Recent) {
    const [old] = await db.get<Job>(jobTable(workspace), [item.jobId]);
    if (!old) return;
    if (old.status === 'sending' && Date.now() - old.updatedAt < 120000) return;
    await clean(workspace, item.jobId, old);
    if (old.cleaned) await db.delete(jobTable(workspace), [item.jobId]);
}
async function configuredSecret(auth: Auth, linked?: Connection | null) {
    const names = await secrets.listSecretNames();
    if (names.includes(secretName(auth))) return secretName(auth);
    // Existing grants are usable only with their previously verified sender.
    const previous = linked === undefined ? await connection(auth.id) : linked;
    if (previous?.senderEmail.toLowerCase() === auth.space.settings.senderEmail.toLowerCase() && names.includes(legacySecretName(auth.id))) return legacySecretName(auth.id);
    return null;
}
async function transport(auth: Auth, linked?: Connection | null) {
    const selected = provider(auth.space.settings.senderEmail);
    if (!selected) throw new MailProblem('手机独立发送目前支持 163、QQ、Foxmail 和 Gmail。');
    const name = await configuredSecret(auth, linked);
    if (!name) throw new MailProblem('请先为当前发件邮箱完成一次独立授权，再连接邮箱。', 409);
    let password: string;
    try { password = (await secrets.readSecret(name)).replace(/\s+/g, ''); }
    catch { throw new MailProblem('请先完成一次发件邮箱授权，再点击连接发件邮箱。', 409); }
    if (!password || password.length > 512) throw new MailProblem('授权信息不完整，请重新连接邮箱。', 409);
    return nodemailer.createTransport({
        host: selected.host, port: 465, secure: true,
        auth: { user: auth.space.settings.senderEmail, pass: password },
        connectionTimeout: 10000, greetingTimeout: 10000, socketTimeout: 45000,
        logger: false, debug: false, disableFileAccess: true, disableUrlAccess: true,
    });
}
function deliveryError(cause: unknown) {
    const e = cause as { code?: string; responseCode?: number };
    if (e.code === 'EAUTH') return { certain: true, message: '邮箱未接受授权，请重新生成授权码并连接。' };
    if (e.code === 'EENVELOPE' || e.code === 'EMESSAGE' || (e.responseCode && e.responseCode >= 400))
        return { certain: true, message: '发件邮箱拒绝了这次发送，请检查 Kindle 地址、附件大小及邮箱限制。' };
    return { certain: false, message: '发送结果暂不确定，请先查看发件邮箱和 Kindle，避免重复发送。' };
}
function publicJob(id: string, job: Job) {
    const stale = job.status === 'sending' && Date.now() - job.updatedAt > 120000;
    return { jobId: id, title: job.title, status: stale ? 'uncertain' : job.status, nextChunk: job.nextChunk, chunks: job.chunks,
        message: stale ? '发送结果暂不确定，请先查看发件邮箱和 Kindle，避免重复发送。' : job.message };
}
function verifyEpub(bytes: Buffer) {
    // The client repairer emits a first, uncompressed mimetype entry.
    if (bytes.length < 80 || bytes.readUInt32LE(0) !== 0x04034b50 || bytes.readUInt16LE(8) !== 0 ||
        bytes.readUInt16LE(26) !== 8 || bytes.readUInt16LE(28) !== 0 || bytes.subarray(30, 38).toString() !== 'mimetype' ||
        bytes.subarray(38, 58).toString() !== 'application/epub+zip') throw new MailProblem('附件不是已校验的 EPUB 修复版。');
    let end = -1;
    for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 65557); i--)
        if (bytes.readUInt32LE(i) === 0x06054b50 && i + 22 + bytes.readUInt16LE(i + 20) === bytes.length) { end = i; break; }
    if (end < 0) throw new MailProblem('EPUB 文件打包不完整，请重新修复。');
    const entries = bytes.readUInt16LE(end + 10);
    let offset = bytes.readUInt32LE(end + 16), expanded = 0;
    if (entries < 3 || entries > 15000) throw new MailProblem('EPUB 内容数量超出支持范围。');
    let container = false, packageFile = false;
    for (let i = 0; i < entries; i++) {
        if (offset + 46 > end || bytes.readUInt32LE(offset) !== 0x02014b50) throw new MailProblem('EPUB 目录校验失败。');
        const count = bytes.readUInt32LE(offset + 24), length = bytes.readUInt16LE(offset + 28);
        const name = bytes.subarray(offset + 46, offset + 46 + length).toString('utf8');
        expanded += count;
        if ((bytes.readUInt16LE(offset + 8) & 1) || count > 32 * 1024 * 1024 || expanded > 180 * 1024 * 1024) throw new MailProblem('EPUB 内容过大或包含加密内容。');
        container ||= name === 'META-INF/container.xml'; packageFile ||= /\.opf$/i.test(name);
        offset += 46 + length + bytes.readUInt16LE(offset + 30) + bytes.readUInt16LE(offset + 32);
    }
    if (!container || !packageFile) throw new MailProblem('EPUB 缺少必要的书籍目录。');
}
export function mailRoutes(authorize: Authorize): RouterRoutes {
    const route = (action: (auth: Auth, input: Record<string, unknown>) => ReturnType<RouterMiddleware>): RouterMiddleware => async ({ body, ...context }) => {
        void context;
        const input = object(body), auth = await authorize(input);
        if (!auth) return error('请先配对或开启设备同步，再连接发件邮箱。', 401);
        try { return await action(auth, input); }
        catch (e) {
            if (e instanceof MailProblem) return error(e.message, e.status);
            const code = e as { name?: string; code?: string; status?: number; statusCode?: number };
            if (code.code === 'AppDatabaseQuotaExceeded' || code.name === 'AppDatabaseQuotaExceeded' || code.status === 429 || code.statusCode === 429)
                return error('请求过多，已暂停操作，请稍后手动重试。', 429);
            return error('服务暂时不可用，修复版仍保留在手机，请稍后重试。', 503);
        }
    };
    const getJob = async (auth: Auth, input: Record<string, unknown>) => {
        if (typeof input.jobId !== 'string' || input.jobId.length > 100) throw new MailProblem('发送记录不正确。');
        const [job] = await db.get<Job>(jobTable(auth.id), [input.jobId]);
        if (!job || job.workspaceId !== auth.id) throw new MailProblem('发送记录不存在，请重新选择修复版。', 404);
        return { id: input.jobId, job };
    };
    return {
        'POST /api/mail/status': [route(async auth => {
            const selected = provider(auth.space.settings.senderEmail), linked = await connection(auth.id);
            const available = await configuredSecret(auth, linked);
            if (linked) for (const item of linked.recent) {
                const [job] = await db.get<Job>(jobTable(auth.id), [item.jobId]);
                if (job && !job.cleaned && (job.expiresAt < Date.now() || ['accepted', 'failed', 'uncertain'].includes(job.status))) await clean(auth.id, item.jobId, job);
            }
            return json({ configured: !!available, connected: !!linked?.enabled && linked.senderEmail.toLowerCase() === auth.space.settings.senderEmail.toLowerCase(),
                provider: selected?.name || '', maxFileBytes: selected?.maxFileBytes || 0, chunkBytes,
                senderEmail: auth.space.settings.senderEmail, kindleEmail: auth.space.settings.kindleEmail });
        })],
        'POST /api/mail/connect': [route(async auth => {
            const old = await connection(auth.id);
            const client = await transport(auth, old);
            try { await client.verify(); }
            catch (e) {
                if ((e as { code?: string }).code === 'EAUTH' && old) await saveConnection(auth.id, { ...old, enabled: false }, old.id);
                const failure = deliveryError(e);
                throw new MailProblem(failure.certain ? failure.message : '暂时无法连接发件邮箱，请检查授权或稍后重试。', 422);
            }
            finally { client.close(); }
            await saveConnection(auth.id, { senderEmail: auth.space.settings.senderEmail, enabled: true, verifiedAt: Date.now(), recent: old?.recent || [] }, old?.id);
            return json({ connected: true, provider: provider(auth.space.settings.senderEmail)?.name });
        })],
        'POST /api/mail/disconnect': [route(async auth => {
            const old = await connection(auth.id);
            if (old) await saveConnection(auth.id, { ...old, enabled: false }, old.id);
            return json({ connected: false });
        })],
        'POST /api/mail/upload': [route(async (auth, input) => {
            const linked = await connection(auth.id), selected = provider(auth.space.settings.senderEmail);
            if (!linked?.enabled || linked.senderEmail.toLowerCase() !== auth.space.settings.senderEmail.toLowerCase()) throw new MailProblem('请先连接手机端发件邮箱。', 409);
            if (!selected) throw new MailProblem('此发件邮箱暂不支持手机独立发送。');
            if (!/^[^\s@]+@(?:free\.)?kindle\.(?:com|cn)$/i.test(auth.space.settings.kindleEmail)) throw new MailProblem('请填写 Kindle 专属接收邮箱。');
            if (input.senderEmail !== auth.space.settings.senderEmail || input.kindleEmail !== auth.space.settings.kindleEmail) throw new MailProblem('邮箱设置尚未同步，请先同步后再发送。', 409);
            if (typeof input.requestId !== 'string' || !/^[a-f0-9-]{36}$/.test(input.requestId) || typeof input.digest !== 'string' || !/^[a-f0-9]{64}$/.test(input.digest) || typeof input.bytes !== 'number' || !Number.isInteger(input.bytes) || input.bytes <= 0 || input.bytes > selected.maxFileBytes) throw new MailProblem('附件大小或校验信息不正确。');
            const previous = linked.recent.find(item => item.requestId === input.requestId);
            if (previous) {
                const [job] = await db.get<Job>(jobTable(auth.id), [previous.jobId]);
                if (job && job.digest === input.digest && job.bytes === input.bytes) return json(publicJob(previous.jobId, job));
                throw new MailProblem('这次发送记录已失效，请重新选择书籍。', 409);
            }
            const name = typeof input.name === 'string' ? input.name.replace(/[\/\\\x00-\x1f]/g, '_').slice(0,180) : '';
            if (!/\.epub$/i.test(name)) throw new MailProblem('仅支持修复后的 EPUB 附件。');
            const now = Date.now();
            const job: Job = { workspaceId: auth.id, requestId: input.requestId, name, title: typeof input.title === 'string' ? input.title.replace(/[\r\n\x00]/g, ' ').slice(0,120) : name,
                bytes: input.bytes, digest: input.digest, chunks: Math.ceil(input.bytes / chunkBytes), nextChunk: 0,
                senderEmail: auth.space.settings.senderEmail, kindleEmail: auth.space.settings.kindleEmail,
                createdAt: now, updatedAt: now, expiresAt: now + 30 * 60 * 1000, status: 'uploading', message: '正在上传修复版', cleaned: false };
            const [id] = await db.add(jobTable(auth.id), [{ ...job }]);
            if (!id) throw new MailProblem('无法准备邮件，请稍后重试。', 503);
            const retained = [...linked.recent, { requestId: input.requestId, jobId: id }];
            await saveConnection(auth.id, { ...linked, recent: retained.slice(-4) }, linked.id);
            for (const older of retained.slice(0, -4)) await prune(auth.id, older);
            return json(publicJob(id, job));
        })],
        'POST /api/mail/chunk': [route(async (auth, input) => {
            const { id, job } = await getJob(auth, input);
            if (job.status !== 'uploading' || job.expiresAt < Date.now()) throw new MailProblem('上传已结束或过期，请重新选择书籍。', 409);
            if (typeof input.index !== 'number' || !Number.isInteger(input.index) || input.index < 0 || input.index >= job.chunks) throw new MailProblem('上传片段不正确。');
            if (input.index < job.nextChunk) return json(publicJob(id, job));
            if (input.index !== job.nextChunk) throw new MailProblem('上传顺序不正确，请重试。', 409);
            if (typeof input.content !== 'string' || input.content.length > 700000 || !/^[A-Za-z0-9+/]*={0,2}$/.test(input.content)) throw new MailProblem('上传内容不正确。');
            const bytes = Buffer.from(input.content, 'base64'), expected = Math.min(chunkBytes, job.bytes - input.index * chunkBytes);
            if (bytes.length !== expected) throw new MailProblem('上传片段不完整。');
            const [ok] = await storage.write([{ path: partPath(auth.id, id, input.index), content: input.content, contentType: 'application/octet-stream' }]);
            if (!ok) throw new MailProblem('上传失败，修复版仍保留在手机。', 503);
            job.nextChunk++; job.updatedAt = Date.now(); await updateJob(auth.id, id, job);
            return json(publicJob(id, job));
        })],
        'POST /api/mail/job': [route(async (auth, input) => { const { id, job } = await getJob(auth, input); return json(publicJob(id, job)); })],
        'POST /api/mail/send': [route(async (auth, input) => {
            const { id, job } = await getJob(auth, input);
            if (job.status !== 'uploading') return json(publicJob(id, job));
            if (job.expiresAt < Date.now() || job.nextChunk !== job.chunks) throw new MailProblem('附件尚未完整上传或已过期。', 409);
            const linked = await connection(auth.id);
            if (!linked?.enabled || linked.senderEmail.toLowerCase() !== job.senderEmail.toLowerCase() || auth.space.settings.senderEmail !== job.senderEmail || auth.space.settings.kindleEmail !== job.kindleEmail) throw new MailProblem('邮箱设置已变化，请重新确认收件地址后发送。', 409);
            const parts = await storage.read(Array.from({ length: job.chunks }, (_, i) => partPath(auth.id, id, i)));
            if (parts.some(part => !part.content)) throw new MailProblem('附件上传不完整，请重试。', 409);
            const bytes = Buffer.concat(parts.map(part => Buffer.from(part.content!, 'base64')));
            if (bytes.length !== job.bytes || createHash('sha256').update(bytes).digest('hex') !== job.digest) throw new MailProblem('附件校验未通过，请重新修复。');
            verifyEpub(bytes);
            const client = await transport(auth, linked);
            job.status = 'sending'; job.updatedAt = Date.now(); job.message = '正在提交到发件邮箱'; await updateJob(auth.id, id, job);
            try {
                const sent = await client.sendMail({ from: job.senderEmail, to: job.kindleEmail, subject: job.title,
                    messageId: `<${id}@${job.senderEmail.split('@')[1]}>`, text: '已检查并修复的 EPUB 书籍，请添加到我的 Kindle。',
                    attachments: [{ filename: job.name, content: bytes, contentType: 'application/epub+zip' }] });
                if (!sent.accepted.map(String).some((value: string) => value.toLowerCase() === job.kindleEmail.toLowerCase())) throw new MailProblem('邮箱未接受 Kindle 收件地址。');
                job.status = 'accepted'; job.message = '邮件已提交，等待 Amazon 处理并同步到 Kindle。';
            } catch (e) {
                const failure = deliveryError(e); job.status = failure.certain ? 'failed' : 'uncertain'; job.message = failure.message;
            } finally { client.close(); }
            job.updatedAt = Date.now(); await updateJob(auth.id, id, job);
            await clean(auth.id, id, job);
            return json(publicJob(id, job));
        })],
    };
}

