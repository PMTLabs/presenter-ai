import { create } from "zustand";

export interface AuthUser {
  id: string;
  email: string;
  displayName: string | null;
  role: string;
}

export interface AuthTokenResponse {
  accessToken: string;
  expiresAt: string;
  user: AuthUser;
}

let accessToken: string | null = null;
let refreshPromise: Promise<boolean> | null = null;

export function getAccessToken() {
  return accessToken;
}

export function hasAccessToken() {
  return accessToken !== null;
}

function saveSession(response: AuthTokenResponse) {
  accessToken = response.accessToken;
  useAuthStore.setState({ user: response.user });
}

export function setAuthSession(response: AuthTokenResponse) {
  saveSession(response);
}

export function clearAuthSession() {
  accessToken = null;
  useAuthStore.setState({ user: null });
}

export function refreshAuth(): Promise<boolean> {
  if (refreshPromise) return refreshPromise;
  refreshPromise = (async () => {
    const response = await fetch("/v1/auth/refresh", {
      method: "POST",
      credentials: "include",
      headers: { Accept: "application/json" },
    });
    if (!response.ok) return false;
    saveSession((await response.json()) as AuthTokenResponse);
    return true;
  })().finally(() => {
    refreshPromise = null;
  });
  return refreshPromise;
}

export interface AuthState {
  user: AuthUser | null;
  signInDev: () => Promise<void>;
  signOut: () => Promise<void>;
  setSession: (response: AuthTokenResponse) => void;
}

export const useAuthStore = create<AuthState>(() => ({
  user: null,
  signInDev: async () => {
    // This endpoint is intentionally absent from the checked-in OpenAPI document:
    // it is mapped only for a development host, so use a plain typed fetch here.
    const response = await fetch("/v1/auth/dev/sign-in", {
      method: "POST",
      headers: { Accept: "application/json" },
    });
    if (!response.ok) throw new Error("Development sign-in failed.");
    saveSession((await response.json()) as AuthTokenResponse);
  },
  signOut: async () => {
    try {
      await fetch("/v1/auth/logout", {
        method: "POST",
        credentials: "include",
        headers: { Accept: "application/json" },
      });
    } finally {
      clearAuthSession();
    }
  },
  setSession: saveSession,
}));
