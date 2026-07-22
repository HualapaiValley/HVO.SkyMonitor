import { createReadStream } from "node:fs";
import { stat } from "node:fs/promises";
import { createServer } from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.dirname(fileURLToPath(import.meta.url));
const host = process.env.HOST ?? "0.0.0.0";
const port = Number.parseInt(process.env.PORT ?? process.argv[2] ?? "4173", 10);
const contentTypes = new Map([
    [".css", "text/css; charset=utf-8"],
    [".html", "text/html; charset=utf-8"],
    [".js", "text/javascript; charset=utf-8"],
    [".mjs", "text/javascript; charset=utf-8"],
    [".png", "image/png"],
    [".svg", "image/svg+xml; charset=utf-8"]
]);

if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error("PORT must be an integer between 1 and 65535.");
}

const server = createServer(async (request, response) => {
    try {
        const requestUrl = new URL(request.url ?? "/", `http://${request.headers.host ?? "localhost"}`);
        const pathname = decodeURIComponent(requestUrl.pathname);
        const requestedPath = pathname === "/" ? "index.html" : pathname.slice(1);
        const filePath = path.resolve(root, requestedPath);
        const relativePath = path.relative(root, filePath);

        if (relativePath.startsWith("..") || path.isAbsolute(relativePath)) {
            response.writeHead(403).end("Forbidden");
            return;
        }

        const file = await stat(filePath);
        if (!file.isFile()) {
            response.writeHead(404).end("Not found");
            return;
        }

        response.writeHead(200, {
            "Cache-Control": "no-store",
            "Content-Length": file.size,
            "Content-Type": contentTypes.get(path.extname(filePath)) ?? "application/octet-stream",
            "X-Content-Type-Options": "nosniff"
        });
        createReadStream(filePath).pipe(response);
    } catch (error) {
        const status = error?.code === "ENOENT" ? 404 : 400;
        response.writeHead(status).end(status === 404 ? "Not found" : "Bad request");
    }
});

server.listen(port, host, () => {
    console.log(`CameraAgent UI mockups: http://localhost:${port}`);
});
