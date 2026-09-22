import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export interface AuthUser {
  id: string;
  email: string;
  displayName: string;
}

interface AuthState {
  user: AuthUser | null;
  signInDev: () => void;
  signOut: () => void;
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      user: null,
      signInDev: () => set({
        user: { id: 'dev-user', email: 'dev@presenter-ai.local', displayName: 'Dev user' },
      }),
      signOut: () => set({ user: null }),
    }),
    { name: 'presenter-auth' },
  ),
);
