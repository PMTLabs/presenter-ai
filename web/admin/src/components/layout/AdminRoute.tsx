// Copied from InkSpoke web/admin/src/components/layout/AdminRoute.tsx @ b83e691f
import type { ReactNode } from 'react';
import { Navigate } from 'react-router-dom';
import { useAuthStore } from '@presenter/shared';

export function AdminRoute({ children }: { children: ReactNode }) {
  const user = useAuthStore((state) => state.user);
  if (user?.role !== "admin") return <Navigate to="/login" replace />;
  return <>{children}</>;
}
