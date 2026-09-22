export function apiBaseUrl(): string {
  const env = (import.meta as ImportMeta & { env?: Record<string, string | undefined> }).env;
  const processEnv = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env;
  return (processEnv?.VITE_API_URL ?? env?.VITE_API_URL ?? "").replace(/\/+$/, "");
}

export function apiUrl(path: string, baseUrl = apiBaseUrl()): string {
  return baseUrl ? `${baseUrl}${path}` : path;
}
