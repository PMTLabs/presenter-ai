import { useEffect, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import apiClient from "@presenter/shared/api";
import { errorMessages, hasAccessToken, isProblem, useAuthStore } from "@presenter/shared";

const redeemedCodes = new Set<string>();

export function AuthCallback() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const setSession = useAuthStore((state) => state.setSession);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const code = params.get("code");
    const state = params.get("state");
    if (!code || redeemedCodes.has(code)) return;
    redeemedCodes.add(code);

    const verifier = state ? sessionStorage.getItem(`presenter-sso:${state}`) : null;
    if (state) sessionStorage.removeItem(`presenter-sso:${state}`);
    if (!verifier) {
      setError("Sign-in could not be completed. Please try again.");
      return;
    }

    void apiClient
      .POST("/v1/auth/sso/token", { body: { code, codeVerifier: verifier } })
      .then(({ data, error: requestError }) => {
        if (data) {
          setSession(data);
          navigate("/", { replace: true });
          return;
        }
        const problem: unknown = requestError;
        if (isProblem(problem) && problem.code === "auth.sso_code_used" && hasAccessToken()) {
          navigate("/", { replace: true });
          return;
        }
        setError(isProblem(problem) ? (errorMessages[problem.code] ?? problem.detail ?? problem.title) : "Sign-in could not be completed. Please try again.");
      })
      .catch(() => setError("Sign-in could not be completed. Please try again."));
  }, [navigate, params, setSession]);

  if (error) return <main className="p-8 text-red-700 dark:text-red-200">{error}</main>;
  return <main className="p-8 text-gray-600 dark:text-gray-300">Completing sign-in…</main>;
}
