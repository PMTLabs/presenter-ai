import { create } from "zustand";
import { apiBaseUrl, apiUrl } from "../api/baseUrl";

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
let sessionGeneration = 0;

export function getAccessToken() {
  return accessToken;
}

export function hasAccessToken() {
  return accessToken !== null;
}

function saveSession(response: AuthTokenResponse) {
  sessionGeneration++;
  accessToken = response.accessToken;
  useAuthStore.setState({ user: response.user });
}

export function setAuthSession(response: AuthTokenResponse) {
  saveSession(response);
}

export function clearAuthSession() {
  sessionGeneration++;
  accessToken = null;
  useAuthStore.setState({ user: null });
}

// All callers, including startup and the API interceptor, share this flight so a rotating cookie is redeemed once.
export function refreshAuth(baseUrl = apiBaseUrl()): Promise<boolean> {
  if (refreshPromise) return refreshPromise;
  const generation = sessionGeneration;
  refreshPromise = (async () => {
    const response = await fetch(apiUrl("/v1/auth/refresh", baseUrl), {
      method: "POST",
      credentials: "include",
      headers: { Accept: "application/json" },
    });
    if (!response.ok) return false;
    const session = (await response.json()) as AuthTokenResponse;
    if (sessionGeneration !== generation) return hasAccessToken();
    saveSession(session);
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
    const response = await fetch(apiUrl("/v1/auth/dev/sign-in"), {
      method: "POST",
      headers: { Accept: "application/json" },
    });
    if (!response.ok) throw new Error("Development sign-in failed.");
    saveSession((await response.json()) as AuthTokenResponse);
  },
  signOut: async () => {
    sessionGeneration++;
    try {
      await refreshPromise?.catch(() => undefined);
      await fetch(apiUrl("/v1/auth/logout"), {
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
