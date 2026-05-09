export interface RetryOptions {
  maxAttempts?: number;
  initialDelayMs?: number;
  maxDelayMs?: number;
  shouldRetry?: (err: unknown, attempt: number) => boolean;
  onRetry?: (err: unknown, attempt: number, delayMs: number) => void;
}

export async function withRetry<T>(fn: () => Promise<T>, opts: RetryOptions = {}): Promise<T> {
  const {
    maxAttempts = 3,
    initialDelayMs = 500,
    maxDelayMs = 8000,
    shouldRetry = () => true,
    onRetry,
  } = opts;

  let lastErr: unknown;
  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    try {
      return await fn();
    } catch (err) {
      lastErr = err;
      if (attempt === maxAttempts || !shouldRetry(err, attempt)) throw err;
      const delay = Math.min(initialDelayMs * 2 ** (attempt - 1), maxDelayMs);
      onRetry?.(err, attempt, delay);
      await sleep(delay);
    }
  }
  throw lastErr;
}

export function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
