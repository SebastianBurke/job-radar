import type { SourceModule } from '../common/types.js';
import { gcjobsSource } from './gcjobs.js';

// Source modules register themselves here. Add an import for each new
// source module and append it to SOURCES.
export const SOURCES: readonly SourceModule[] = [gcjobsSource];
