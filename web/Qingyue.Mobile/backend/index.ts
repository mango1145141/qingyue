import { db, error, json, router } from '@appdeploy/sdk';
import { mailRoutes } from './mail';

type Settings = { senderEmail: string; kindleEmail: string; autoSend: boolean; autoRepair: boolean; rememberHistory: boolean };
type Space = { keyHashes: string[]; settings: Settings; revision: string; pairHash?: string; pairExpires?: number };
const table = 'private-settings';
const fields = ['senderEmail', 'kindleEmail', 'autoSend', 'autoRepair', 'rememberHistory'] as const;
const defaults: Settings = { senderEmail: '', kindleEmail: '', autoSend: true, autoRepair: true, rememberHistory: true };
const token = () => Array.from(crypto.getRandomValues(new Uint8Array(24)), b => b.toString(16).padStart(2, '0')).join('');
const hash = async (value: string) => Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value))), b => b.toString(16).padStart(2, '0')).join('');
function object(value: unknown): Record<string, unknown> {
    return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
}
function patch(value: unknown): Partial<Settings> {
    const input = object(value);
    const output: Record<string, string | boolean> = {};
    for (const name of fields) {
        if (!(name in input)) continue;
        if (name === 'senderEmail' || name === 'kindleEmail') {
            if (typeof input[name] !== 'string') throw new Error('邮箱格式不正确。');
            const email = (input[name] as string).trim();
            if (email.length > 254 || (email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email))) throw new Error('邮箱格式不正确。');
            output[name] = email;
        } else {
            if (typeof input[name] !== 'boolean') throw new Error('设置格式不正确。');
            output[name] = input[name] as boolean;
        }
    }
    return output;
}
async function authorize(input: Record<string, unknown>) {
    if (typeof input.workspaceId !== 'string' || input.workspaceId.length > 100 || typeof input.accessKey !== 'string' || !/^[a-f0-9]{48}$/.test(input.accessKey)) return null;
    const [space] = await db.get<Space>(table, [input.workspaceId]);
    const keyHash = await hash(input.accessKey);
    if (!space || !space.keyHashes.includes(keyHash)) return null;
    return { id: input.workspaceId, space, keyHash };
}
const publicState = (space: Space) => ({ settings: space.settings, revision: space.revision, devices: space.keyHashes.length });
export const handler = router({
    ...mailRoutes(authorize),
    'POST /api/sync/create': [async ({ body }) => {
        let settings: Settings;
        try { settings = { ...defaults, ...patch(object(body).settings) }; } catch (e) { return error((e as Error).message, 400); }
        const accessKey = token();
        const space: Space = { keyHashes: [await hash(accessKey)], settings, revision: crypto.randomUUID() };
        const [workspaceId] = await db.add(table, [{ ...space }]);
        if (!workspaceId) return error('暂时无法开启同步，请稍后再试。', 503);
        return json({ workspaceId, accessKey, ...publicState(space) });
    }],
    'POST /api/sync/state': [async ({ body }) => {
        const auth = await authorize(object(body));
        return auth ? json(publicState(auth.space)) : error('此设备未配对或已断开，请重新配对。', 401);
    }],
    'POST /api/sync/save': [async ({ body }) => {
        const input = object(body);
        const auth = await authorize(input);
        if (!auth) return error('此设备未配对或已断开，请重新配对。', 401);
        if (input.revision !== auth.space.revision) return json({ conflict: true, ...publicState(auth.space) }, 409);
        let changes: Partial<Settings>;
        try { changes = patch(input.patch); } catch (e) { return error((e as Error).message, 400); }
        const next: Space = { ...auth.space, settings: { ...auth.space.settings, ...changes }, revision: crypto.randomUUID() };
        const [ok] = await db.update(table, [{ id: auth.id, record: { ...next } }]);
        return ok ? json(publicState(next)) : error('同步保存失败，原设置仍保留。', 503);
    }],
    'POST /api/sync/pair': [async ({ body }) => {
        const auth = await authorize(object(body));
        if (!auth) return error('此设备未配对或已断开，请重新配对。', 401);
        const secret = token();
        const expiresAt = Date.now() + 5 * 60 * 1000;
        const next = { ...auth.space, pairHash: await hash(secret), pairExpires: expiresAt };
        const [ok] = await db.update(table, [{ id: auth.id, record: next }]);
        return ok ? json({ pairCode: `${auth.id}.${secret}`, expiresAt }) : error('无法生成配对信息，请重试。', 503);
    }],
    'POST /api/sync/claim': [async ({ body }) => {
        const code = object(body).pairCode;
        if (typeof code !== 'string' || code.length > 160) return error('配对信息格式不正确。', 400);
        const [id, secret, extra] = code.split('.');
        if (!id || !/^[a-f0-9]{48}$/.test(secret || '') || extra !== undefined) return error('配对信息格式不正确。', 400);
        const [space] = await db.get<Space>(table, [id]);
        if (!space || !space.pairHash || !space.pairExpires || space.pairExpires < Date.now() || await hash(secret) !== space.pairHash) return error('配对信息已使用或已过期，请在另一台设备重新生成。', 400);
        if (space.keyHashes.length >= 8) return error('最多连接 8 台设备，请先断开不使用的设备。', 400);
        const accessKey = token();
        const next: Space = { ...space, keyHashes: [...space.keyHashes, await hash(accessKey)], pairHash: '', pairExpires: 0 };
        const [ok] = await db.update(table, [{ id, record: { ...next } }]);
        return ok ? json({ workspaceId: id, accessKey, ...publicState(next) }) : error('配对失败，请重试。', 503);
    }],
    'POST /api/sync/disconnect': [async ({ body }) => {
        const auth = await authorize(object(body));
        if (!auth) return error('此设备已断开。', 401);
        const next = { ...auth.space, keyHashes: auth.space.keyHashes.filter(key => key !== auth.keyHash) };
        const [ok] = await db.update(table, [{ id: auth.id, record: next }]);
        return ok ? json({ disconnected: true }) : error('断开失败，请重试。', 503);
    }],
});

