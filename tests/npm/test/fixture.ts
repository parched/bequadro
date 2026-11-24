// A temporary project with a schema directory, for the package and plugin tests.
import { mkdtempSync, mkdirSync, readFileSync, realpathSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

export const shapes = `namespace Shapes;

struct Point { x: float; y: float; }

table Circle { center: Point; radius: float = 1; }
`;

export class Project {
  // Resolve macOS symlinks and Windows short names before comparing paths with Vite diagnostics.
  readonly root: string = realpathSync.native(mkdtempSync(join(tmpdir(), "bequadro-npm-")));

  constructor(schema = shapes) {
    mkdirSync(join(this.root, "schemas"));
    this.writeSchema(schema);
  }

  get schema(): string {
    return join(this.root, "schemas", "shapes.fbs");
  }

  get generated(): string {
    return join(this.root, "generated");
  }

  /** Saves the schema in place, as some editors do: a reader can see it empty or partly written. */
  writeSchema(text: string): void {
    writeFileSync(this.schema, text);
  }

  /** Saves the schema atomically, as editors that write a temporary file and rename it do: a reader never sees it
   * partly written. */
  replaceSchema(text: string): void {
    const temporary = `${this.schema}.tmp`;
    writeFileSync(temporary, text);
    renameSync(temporary, this.schema);
  }

  read(path: string): string | undefined {
    try {
      return readFileSync(join(this.root, path), "utf8");
    } catch {
      return undefined;
    }
  }

  write(path: string, text: string): void {
    writeFileSync(join(this.root, path), text);
  }

  [Symbol.dispose](): void {
    rmSync(this.root, { recursive: true, force: true, maxRetries: 3 });
  }
}

/** Waits until condition holds, polling, or fails after a timeout. */
export async function until(condition: () => boolean, description: string, timeout = 15_000): Promise<void> {
  const end = Date.now() + timeout;
  while (!condition()) {
    if (Date.now() > end) {
      throw new Error(`Timed out waiting until ${description}.`);
    }

    await new Promise((resolve) => setTimeout(resolve, 25));
  }
}
