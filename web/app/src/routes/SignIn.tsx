import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import apiClient from "@presenter/shared/api";
import { errorMessages, isProblem, useAuthStore } from "@presenter/shared";

function randomValue(length = 32) {
  const bytes = new Uint8Array(length);
  crypto.getRandomValues(bytes);
  return btoa(String.fromCharCode(...bytes))
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replaceAll("=", "");
}

async function challenge(verifier: string) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return btoa(String.fromCharCode(...new Uint8Array(digest)))
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replaceAll("=", "");
}

export function SignIn() {
  const navigate = useNavigate();
  const signInDev = useAuthStore((state) => state.signInDev);
  const [providers, setProviders] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void apiClient.GET("/v1/auth/sso/providers").then(({ data, error: requestError }) => {
      if (requestError) {
        const problem: unknown = requestError;
        setError(isProblem(problem) ? (errorMessages[problem.code] ?? problem.detail ?? problem.title) : "Unable to load sign-in providers.");
        return;
      }
      setProviders(data?.items.map((provider) => provider.id) ?? []);
    });
  }, []);

  const signIn = async (provider: string) => {
    setBusy(true);
    setError(null);
    try {
      const verifier = randomValue();
      const state = randomValue(16);
      sessionStorage.setItem(`presenter-sso:${state}`, verifier);
      const params = new URLSearchParams({
        redirect_uri: `${window.location.origin}/auth/callback`,
        code_challenge: await challenge(verifier),
        state,
      });
      window.location.assign(`/v1/auth/sso/${encodeURIComponent(provider)}/authorize?${params}`);
    } catch {
      setBusy(false);
      setError("Unable to start sign-in.");
    }
  };

  return (
    <main className="flex min-h-[calc(100vh-4rem)] items-center justify-center bg-gray-50 px-6 text-gray-900 dark:bg-gray-950 dark:text-gray-100">
      <section className="w-full max-w-md rounded-xl bg-white p-8 shadow dark:bg-gray-900">
        <h1 className="text-2xl font-semibold">Sign in to Presenter AI</h1>
        {error && <p className="mt-4 rounded-lg bg-red-50 p-3 text-red-700 dark:bg-red-950 dark:text-red-200">{error}</p>}
        <div className="mt-6 grid gap-3">
          {providers.map((provider) => (
            <button key={provider} disabled={busy} onClick={() => void signIn(provider)} className="rounded-lg border border-gray-300 px-4 py-2 text-left dark:border-gray-700">
              Continue with {provider}
            </button>
          ))}
          {import.meta.env.DEV && (
            <button
              disabled={busy}
              onClick={() => void signInDev().then(() => navigate("/"), () => setError("Development sign-in failed."))}
              className="rounded-lg bg-gray-900 px-4 py-2 text-white dark:bg-white dark:text-gray-900"
            >
              Sign in (Development)
            </button>
          )}
          {providers.length === 0 && !import.meta.env.DEV && <p className="text-sm text-gray-500">No sign-in providers are enabled.</p>}
        </div>
      </section>
    </main>
  );
}
