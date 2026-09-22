import { Link, Route, Routes } from 'react-router-dom';
import { ThemeToggle, useAuthStore } from '@presenter/shared';
import { Library } from './routes/Library';
import { Present } from './routes/Present';

function Header() {
  const user = useAuthStore((state) => state.user);
  const signInDev = useAuthStore((state) => state.signInDev);
  const signOut = useAuthStore((state) => state.signOut);
  return (
    <header className="flex h-16 items-center border-b border-gray-200 bg-white px-6 dark:border-gray-800 dark:bg-gray-900">
      <Link className="font-semibold text-gray-900 dark:text-white" to="/">Presenter AI</Link>
      <div className="ml-auto flex items-center gap-3">
        {user ? <><span className="hidden text-sm text-gray-600 sm:inline dark:text-gray-300">{user.displayName}</span><button className="text-sm text-gray-500 hover:text-gray-900 dark:hover:text-white" onClick={signOut}>Sign out</button></> : <button className="rounded-lg bg-gray-900 px-3 py-2 text-sm font-medium text-white dark:bg-white dark:text-gray-900" onClick={signInDev}>Sign in (Dev)</button>}
        <ThemeToggle />
      </div>
    </header>
  );
}

export function App() {
  return <div className="min-h-screen bg-gray-50 dark:bg-gray-950"><Header /><main className="mx-auto max-w-7xl p-6"><Routes><Route path="/" element={<Library />} /><Route path="/present/:id" element={<Present />} /></Routes></main></div>;
}
