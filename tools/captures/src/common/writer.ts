import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import type { CapturedFile } from './schema.js';
import type { Logger } from './logger.js';

export async function writeCaptureFile(
  capturedDir: string,
  file: CapturedFile,
  logger: Logger,
): Promise<string> {
  await mkdir(capturedDir, { recursive: true });
  const path = join(capturedDir, `${file.source}.json`);
  await writeFile(path, JSON.stringify(file, null, 2) + '\n', 'utf8');
  logger.info(`Wrote ${file.postings.length} postings to ${path}`);
  return path;
}

export async function commitAndPush(
  repoRoot: string,
  capturedDir: string,
  sourceNames: string[],
  logger: Logger,
): Promise<void> {
  const status = await runGit(repoRoot, ['status', '--porcelain', '--', capturedDir]);
  if (!status.stdout.trim()) {
    logger.info('No changes in data/captured/; skipping commit + push.');
    return;
  }
  await runGit(repoRoot, ['add', '--', capturedDir]);
  const date = new Date().toISOString().slice(0, 10);
  const sourceList = sourceNames.length ? sourceNames.join(',') : 'all';
  const msg = `chore(captured): ${date} ${sourceList}`;
  await runGit(repoRoot, ['commit', '-m', msg]);
  await runGit(repoRoot, ['push']);
  logger.info(`Committed and pushed: ${msg}`);
}

interface GitResult {
  stdout: string;
  stderr: string;
}

function runGit(cwd: string, args: string[]): Promise<GitResult> {
  return new Promise((resolve, reject) => {
    const child = spawn('git', args, { cwd, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk: Buffer) => {
      stdout += chunk.toString('utf8');
    });
    child.stderr.on('data', (chunk: Buffer) => {
      stderr += chunk.toString('utf8');
    });
    child.on('error', reject);
    child.on('close', (code) => {
      if (code === 0) resolve({ stdout, stderr });
      else reject(new Error(`git ${args.join(' ')} exited ${code ?? 'null'}: ${stderr || stdout}`));
    });
  });
}
