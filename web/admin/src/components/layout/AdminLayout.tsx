// Copied from InkSpoke web/admin/src/components/layout/AdminLayout.tsx @ b83e691f
import { useState } from 'react';
import { LayoutDashboard, LogOut, Menu, PanelLeftClose, PanelLeftOpen, X } from 'lucide-react';
import { NavLink, Outlet } from 'react-router-dom';
import { ThemeToggle, cn, useAuthStore } from '@presenter/shared';

type NavItem = { to: string; label: string; icon: typeof LayoutDashboard };
const navItems: NavItem[] = [{ to: '/', label: 'Overview', icon: LayoutDashboard }];

export function AdminLayout() {
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [collapsed, setCollapsed] = useState(false);
  const signOut = useAuthStore((state) => state.signOut);

  return (
    <div className="flex h-screen bg-gray-50 text-gray-900 dark:bg-gray-950 dark:text-gray-100">
      {sidebarOpen && <div className="fixed inset-0 z-30 bg-black/50 lg:hidden" onClick={() => setSidebarOpen(false)} />}
      <aside className={cn(
        'fixed inset-y-0 left-0 z-40 flex flex-col bg-gray-900 text-gray-300 transition-all lg:static lg:translate-x-0',
        sidebarOpen ? 'translate-x-0' : '-translate-x-full',
        collapsed ? 'w-16' : 'w-sidebar',
      )}>
        <div className="flex h-16 items-center justify-between px-4">
          {!collapsed && <span className="text-lg font-bold text-white">Presenter AI</span>}
          <button className="text-gray-400 hover:text-white lg:hidden" onClick={() => setSidebarOpen(false)} aria-label="Close menu">
            <X className="h-5 w-5" />
          </button>
          <button className="hidden text-gray-400 hover:text-white lg:block" onClick={() => setCollapsed((value) => !value)} aria-label="Toggle sidebar">
            {collapsed ? <PanelLeftOpen className="h-5 w-5" /> : <PanelLeftClose className="h-5 w-5" />}
          </button>
        </div>
        <nav className="flex-1 px-2 py-2">
          {navItems.map(({ to, label, icon: Icon }) => (
            <NavLink key={to} to={to} end onClick={() => setSidebarOpen(false)} title={collapsed ? label : undefined}
              className={({ isActive }) => cn(
                'flex items-center rounded-lg text-sm font-medium transition-colors',
                collapsed ? 'justify-center px-2 py-2' : 'gap-3 px-3 py-2',
                isActive ? 'bg-gray-800 text-brand-300' : 'text-gray-400 hover:bg-gray-800 hover:text-gray-200',
              )}>
              <Icon className="h-4 w-4 shrink-0" />
              {!collapsed && label}
            </NavLink>
          ))}
        </nav>
        <div className="border-t border-gray-800 p-2">
          <button onClick={signOut} title={collapsed ? 'Sign out' : undefined}
            className={cn('flex w-full items-center rounded-lg text-sm font-medium text-gray-400 transition-colors hover:bg-gray-800 hover:text-gray-200', collapsed ? 'justify-center px-2 py-2' : 'gap-3 px-3 py-2')}>
            <LogOut className="h-4 w-4 shrink-0" />
            {!collapsed && 'Sign out'}
          </button>
        </div>
      </aside>
      <div className="flex flex-1 flex-col overflow-hidden">
        <header className="flex h-16 items-center gap-4 border-b border-gray-200 bg-white px-6 dark:border-gray-800 dark:bg-gray-900">
          <button className="text-gray-600 dark:text-gray-400 lg:hidden" onClick={() => setSidebarOpen(true)} aria-label="Open menu"><Menu className="h-6 w-6" /></button>
          <div className="ml-auto"><ThemeToggle /></div>
        </header>
        <main className="flex-1 overflow-y-auto p-6"><Outlet /></main>
      </div>
    </div>
  );
}
