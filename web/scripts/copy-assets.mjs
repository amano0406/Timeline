import { copyFileSync, cpSync, existsSync, mkdirSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";

function ensureDir(path) {
  mkdirSync(path, { recursive: true });
}

function copyFile(source, destination) {
  ensureDir(dirname(destination));
  copyFileSync(source, destination);
}

function findFile(root, fileName) {
  if (!existsSync(root)) {
    return null;
  }
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isFile() && entry.name === fileName) {
      return path;
    }
    if (entry.isDirectory()) {
      const nested = findFile(path, fileName);
      if (nested) {
        return nested;
      }
    }
  }
  return null;
}

copyFile(
  join("node_modules", "@fortawesome", "fontawesome-free", "css", "all.min.css"),
  join("wwwroot", "vendor", "fontawesome", "css", "all.min.css"),
);
cpSync(
  join("node_modules", "@fortawesome", "fontawesome-free", "webfonts"),
  join("wwwroot", "vendor", "fontawesome", "webfonts"),
  { recursive: true },
);

const twElements = findFile(join("node_modules", "tw-elements"), "tw-elements.umd.min.js")
  ?? findFile(join("node_modules", "tw-elements"), "tw-elements.min.js");

if (twElements) {
  copyFile(twElements, join("wwwroot", "vendor", "tw-elements", "tw-elements.umd.min.js"));
}

copyFile(
  join("node_modules", "chart.js", "dist", "chart.umd.js"),
  join("wwwroot", "vendor", "chart.js", "chart.umd.js"),
);
