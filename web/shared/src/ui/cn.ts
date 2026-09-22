// Copied from InkSpoke web/shared/src/lib/cn.ts @ b83e691f
import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
