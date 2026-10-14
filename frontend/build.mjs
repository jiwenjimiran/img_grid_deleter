import { build } from "esbuild";
import { mkdir } from "node:fs/promises";

const outdir = new URL("../src/DeleteImageGrids/dist/", import.meta.url).pathname.replace(/^\/(.:\/)/, "$1");
await mkdir(outdir, { recursive: true });

await build({
  entryPoints: ["src/index.jsx"],
  bundle: true,
  format: "esm",
  platform: "browser",
  target: "es2022",
  outfile: `${outdir}/bundle.js`,
  minifySyntax: true,
  external: ["@cove/runtime/react", "@cove/runtime/lucide-react"],
  define: { __IGD_DEV_EDITION__: process.env.IGD_DEV_EDITION === "true" ? "true" : "false" },
});

await build({
  entryPoints: ["src/styles.css"],
  bundle: true,
  outfile: `${outdir}/bundle.css`,
});
