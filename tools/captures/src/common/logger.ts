type Level = 'debug' | 'info' | 'warn' | 'error';

const LEVEL_RANK: Record<Level, number> = { debug: 10, info: 20, warn: 30, error: 40 };

function resolveMinLevel(): Level {
  const env = (process.env.JOBRADAR_LOG_LEVEL ?? '').toLowerCase();
  if (env === 'debug' || env === 'info' || env === 'warn' || env === 'error') return env;
  return 'info';
}

const minRank = LEVEL_RANK[resolveMinLevel()];

export interface Logger {
  debug(msg: string, ...args: unknown[]): void;
  info(msg: string, ...args: unknown[]): void;
  warn(msg: string, ...args: unknown[]): void;
  error(msg: string, ...args: unknown[]): void;
  child(prefix: string): Logger;
}

export function createLogger(prefix = ''): Logger {
  const tag = prefix ? ` [${prefix}]` : '';
  const emit = (level: Level, msg: string, args: unknown[]) => {
    if (LEVEL_RANK[level] < minRank) return;
    const line = `[${new Date().toISOString()}] [${level.toUpperCase()}]${tag} ${msg}`;
    if (args.length) console.error(line, ...args);
    else console.error(line);
  };
  return {
    debug: (msg, ...args) => emit('debug', msg, args),
    info: (msg, ...args) => emit('info', msg, args),
    warn: (msg, ...args) => emit('warn', msg, args),
    error: (msg, ...args) => emit('error', msg, args),
    child: (childPrefix) => createLogger(prefix ? `${prefix}/${childPrefix}` : childPrefix),
  };
}
