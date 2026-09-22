import createClient from 'openapi-fetch';
import type { paths } from './generated';

export type { components, paths } from './generated';

export type ApiClient = ReturnType<typeof createApiClient>;

export function createApiClient(baseUrl?: string) {
  const env = (import.meta as ImportMeta & { env?: Record<string, string | undefined> }).env;
  return createClient<paths>({ baseUrl: baseUrl ?? env?.VITE_API_URL ?? '' });
}

const apiClient = createApiClient();
export default apiClient;
