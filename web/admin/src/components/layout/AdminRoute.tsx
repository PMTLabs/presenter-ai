// Copied from InkSpoke web/admin/src/components/layout/AdminRoute.tsx @ b83e691f
import type { ReactNode } from 'react';
import { Navigate } from 'react-router-dom';
import { useAuthStore } from '@presenter/shared';

export function AdminRoute({ children }: { children: ReactNode }) {
  const ready = useAuthStore((state) => state.ready);
  const user = useAuthStore((state) => state.user);
  if (!ready) return null;
  if (user?.role !== "admin") return <Navigate to="/login" replace />;
  return <>{children}</>;
}
