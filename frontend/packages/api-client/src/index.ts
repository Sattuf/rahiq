import type { Problem } from "./types";

export * from "./types";

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    public readonly details?: Record<string, string[]>,
  ) {
    super(code);
  }
}

export async function toApiError(response: Response): Promise<ApiError> {
  let problem: Partial<Problem> = {};
  try {
    problem = (await response.json()) as Partial<Problem>;
  } catch {
    // Not JSON (a proxy error page): keep the status only.
  }

  const code = problem.code ?? (response.status === 429 ? "rate_limited" : `http.${response.status}`);
  return new ApiError(response.status, code, problem.errors);
}

/** Idempotency keys for money-moving requests: one per user intention, reused on retry. */
export function idempotencyKey(): string {
  return crypto.randomUUID().replaceAll("-", "");
}
