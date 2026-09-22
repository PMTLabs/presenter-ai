import { ERROR_CODES, type ErrorCode } from './errorCodes';

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  code: ErrorCode;
  traceId?: string;
  errors?: Record<string, string[]>;
  [key: string]: unknown;
}

export function isProblem(value: unknown): value is ProblemDetails {
  if (!value || typeof value !== 'object') return false;
  const body = value as Record<string, unknown>;
  return typeof body.code === 'string'
    && body.code in ERROR_CODES
    && (typeof body.title === 'string' || typeof body.status === 'number' || typeof body.detail === 'string');
}
