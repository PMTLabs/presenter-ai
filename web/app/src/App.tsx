import { useEffect } from 'react';
import { Link, Navigate, Route, Routes } from 'react-router-dom';
import { refreshAuth, ThemeToggle, useAuthStore } from '@presenter/shared';
import { Library } from './routes/Library';
import { Present } from './routes/Present';
import { SignIn } from './routes/SignIn';
import { AuthCallback } from './routes/AuthCallback';
import { Tools } from './routes/Tools';
import { ToolsOAuthCallback } from './routes/ToolsOAuthCallback';

function Header() {
  const user = useAuthStore((state) => state.user);
  const signOut = useAuthStore((state) => state.signOut);
  return (
    <header
      className="flex h-16 items-center border-b border-gray-200 bg-white px-6 dark:border-gray-800 dark:bg-gray-900"
    >
      <Link className="font-semibold text-gray-900 dark:text-white" to="/">
        Presenter AI
      </Link>
      <div className="ml-auto flex items-center gap-3">
        {user ? (
          <>
            <Link className="text-sm text-gray-600 hover:text-gray-900 dark:text-gray-300 dark:hover:text-white" to="/tools">Tools</Link>
            <span className="hidden text-sm text-gray-600 sm:inline dark:text-gray-300">
              {user.displayName}
            </span>
            <button
              className="text-sm text-gray-500 hover:text-gray-900 dark:hover:text-white"
              onClick={signOut}
            >
              Sign out
            </button>
          </>
        ) : (
          <Link
            className="rounded-lg bg-gray-900 px-3 py-2 text-sm font-medium text-white dark:bg-white dark:text-gray-900"
            to="/login"
          >
            Sign in
          </Link>
        )}
        <ThemeToggle />
      </div>
    </header>
  );
}

export function App() {
  useEffect(() => {
    void refreshAuth();
  }, []);

  return (
    <div className="min-h-screen overflow-x-hidden bg-gray-50 text-gray-900 dark:bg-gray-950 dark:text-gray-100">
      <Header />
      <main className="min-h-[calc(100vh-4rem)]">
        <Routes>
          <Route path="/login" element={<SignIn />} />
          <Route path="/auth/callback" element={<AuthCallback />} />
          <Route path="/tools/oauth/callback" element={<ToolsOAuthCallback />} />
          <Route path="/tools" element={<Tools />} />
          <Route path="/" element={<Library />} />
          <Route path="/present/:id" element={<Present />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </div>
  );
}
