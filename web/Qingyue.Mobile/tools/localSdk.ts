// Local UI preview deliberately cannot access the live mail or sync backend.
export const api = {
  async post() { throw new Error('本地预览不连接在线邮箱和同步服务。'); },
  async get() { throw new Error('本地预览不连接在线邮箱和同步服务。'); },
};
