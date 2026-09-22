import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import apiClient, { type components } from "@presenter/shared/api";
import { errorMessages, isProblem } from "@presenter/shared";

type PresentationRow = components["schemas"]["PresentationSummary"];

export function Library() {
  const [presentations, setPresentations] = useState<PresentationRow[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    void apiClient
      .GET("/api/presentations")
      .then(({ data, error: requestError }) => {
        if (!active) return;
        if (requestError) {
          const problem: unknown = requestError;
          setError(
            isProblem(problem)
              ? (errorMessages[problem.code] ?? problem.detail ?? problem.title)
              : "Unable to load presentations.",
          );
          return;
        }
        if (data) setPresentations(data);
      })
      .catch(() => {
        if (active) setError("Unable to load presentations.");
      });
    return () => {
      active = false;
    };
  }, []);

  return (
    <section className="mx-auto max-w-7xl p-6">
      <h1 className="text-2xl font-bold text-gray-900 dark:text-white">
        Library
      </h1>
      <p className="mt-2 text-gray-600 dark:text-gray-400">
        Choose a presentation to begin.
      </p>
      {error && (
        <p className="mt-6 rounded-lg bg-red-50 p-4 text-red-700 dark:bg-red-950 dark:text-red-200">
          {error}
        </p>
      )}
      {!error && presentations.length === 0 && (
        <p className="mt-8 text-gray-500">No presentations found.</p>
      )}
      <ul className="mt-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {presentations.map((presentation) => (
          <li
            key={presentation.id}
            className="rounded-xl border border-gray-200 bg-white p-5 text-gray-900 shadow-sm dark:border-gray-800 dark:bg-gray-900 dark:text-gray-100"
          >
            <h2 className="font-semibold text-gray-900 dark:text-white">
              {presentation.title ?? presentation.id}
            </h2>
            <p className="mt-2 text-sm text-gray-500">
              {presentation.slideCount ?? 0} slides
            </p>
            {!presentation.error && (
              <Link
                className="mt-4 inline-block text-sm font-medium text-blue-600 hover:underline"
                to={`/present/${encodeURIComponent(presentation.id)}`}
              >
                Present
              </Link>
            )}
          </li>
        ))}
      </ul>
    </section>
  );
}
