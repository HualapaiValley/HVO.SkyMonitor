import { open } from "node:fs/promises";
import { createServer } from "node:http";
import path from "node:path";
import { pipeline } from "node:stream/promises";
import { fileURLToPath } from "node:url";

const root = path.dirname(fileURLToPath(import.meta.url));
const host = process.env.HOST ?? "0.0.0.0";
const port = Number.parseInt(process.env.PORT ?? process.argv[2] ?? "4174", 10);
const contentTypes = new Map([
    [".css", "text/css; charset=utf-8"],
    [".html", "text/html; charset=utf-8"],
    [".js", "text/javascript; charset=utf-8"],
    [".md", "text/markdown; charset=utf-8"],
    [".mjs", "text/javascript; charset=utf-8"],
    [".svg", "image/svg+xml; charset=utf-8"]
]);

if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error("PORT must be an integer between 1 and 65535.");
}

const server = createServer(async (request, response) => {
    let fileHandle;
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

        fileHandle = await open(filePath, "r");
        const file = await fileHandle.stat();
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
        await pipeline(fileHandle.createReadStream({ autoClose: false }), response);
    } catch (error) {
        if (response.headersSent) {
            response.destroy(error);
            return;
        }

        const status = error?.code === "ENOENT" || error?.code === "ENOTDIR"
            ? 404
            : error instanceof URIError || error?.code === "ERR_INVALID_URL" || error?.code === "ERR_INVALID_ARG_VALUE"
                ? 400
                : 500;
        const message = status === 404 ? "Not found" : status === 400 ? "Bad request" : "Internal server error";
        response.writeHead(status).end(message);
    } finally {
        try {
            await fileHandle?.close();
        } catch {
            // The response already reflects any read failure; cleanup cannot change it.
        }
    }
});

server.listen(port, host, () => {
    console.log(`LogicHost UI mockups: http://localhost:${port}`);
});
