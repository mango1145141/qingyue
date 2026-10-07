// AppDeploy supplies this SDK at build/runtime; credentials never enter this declaration.
declare module '@appdeploy/client' {
  export const api: {
    post<T = any>(path: string, body: unknown): Promise<{ data: T }>;
    get<T = any>(path: string): Promise<{ data: T }>;
  };
}
