import { create } from "zustand";
import { persist } from "zustand/middleware";
import { signInDev as createDevUser } from "./devSignIn";

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
      signInDev: () => set({ user: createDevUser() }),
      signOut: () => set({ user: null }),
    }),
    { name: "presenter-auth" },
  ),
);
