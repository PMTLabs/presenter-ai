import { useEffect } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import { AdminLayout } from './components/layout/AdminLayout';
import { AdminRoute } from './components/layout/AdminRoute';
import { ComingSoon } from './pages/ComingSoon';
import { refreshAuth, useAuthStore } from '@presenter/shared';

const isDevelopment = (import.meta as ImportMeta & { env?: { DEV?: boolean } }).env?.DEV === true;

function Login() {
  const signInDev = useAuthStore((state) => state.signInDev);
  const user = useAuthStore((state) => state.user);
  const signOut = useAuthStore((state) => state.signOut);
  if (user?.role === "admin") return <Navigate to="/" replace />;
  return (
    <main className="flex min-h-screen items-center justify-center bg-gray-50 text-gray-900 dark:bg-gray-950 dark:text-gray-100">
      <div className="rounded-xl bg-white p-8 text-center text-gray-900 shadow dark:bg-gray-900 dark:text-gray-100">
        <h1 className="text-xl font-semibold text-gray-900 dark:text-white">Presenter AI admin</h1>
        {isDevelopment && (
          <button className="mt-6 rounded-lg bg-gray-900 px-4 py-2 text-sm font-medium text-white dark:bg-white dark:text-gray-900" onClick={() => void signInDev()}>
            Sign in (Dev)
          </button>
        )}
        {user && <button className="mt-3 block w-full text-sm text-gray-500" onClick={() => void signOut()}>Sign out</button>}
      </div>
    </main>
  );
}

export function App() {
  useEffect(() => {
    void refreshAuth();
  }, []);

  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route element={<AdminRoute><AdminLayout /></AdminRoute>}>
        <Route index element={<ComingSoon />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
