// Independent hosting only; the AppDeploy build retains the real SDK.
type Result<T> = { data: T };
class ApiProblem extends Error {
  response?: { status: number; data: Record<string, unknown> };
  constructor(
    message: string,
    status?: number,
    data?: Record<string, unknown>,
  ) {
    super(message);
    if (status !== undefined) this.response = { status, data: data || {} };
  }
}
const allowed =
  /^\/api\/(?:sync\/(?:create|state|save|pair|claim|disconnect)|mail\/(?:status|connect|disconnect|upload|chunk|job|send))$/;
async function request<T>(
  method: 'GET' | 'POST',
  path: string,
  body?: unknown,
): Promise<Result<T>> {
  if (!allowed.test(path)) throw new ApiProblem('此服务入口不受支持。');
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 90000);
  try {
    const response = await fetch(path, {
      method,
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
      },
      body: method === 'POST' ? JSON.stringify(body ?? {}) : undefined,
      credentials: 'omit',
      cache: 'no-store',
      redirect: 'error',
      signal: controller.signal,
    });
    let data: unknown;
    try {
      data = await response.json();
    } catch {
      throw new ApiProblem('服务响应不完整，请稍后重试。', response.status);
    }
    if (!response.ok) {
      const info =
        data && typeof data === 'object' && !Array.isArray(data)
          ? (data as Record<string, unknown>)
          : {};
      throw new ApiProblem(
        typeof info.error === 'string'
          ? info.error
          : typeof info.message === 'string'
            ? info.message
            : '连接服务暂时不可用，请稍后重试。',
        response.status,
        info,
      );
    }
    return { data: data as T };
  } catch (cause) {
    if (cause instanceof ApiProblem) throw cause;
    throw new ApiProblem(
      '连接服务暂时中断，修复版仍保留在手机。请重试或使用分享菜单。',
    );
  } finally {
    clearTimeout(timer);
  }
}
export const api = {
  post<T = unknown>(path: string, body: unknown) {
    return request<T>('POST', path, body);
  },
  get<T = unknown>(path: string) {
    return request<T>('GET', path);
  },
};
