export async function authorizeRawDownload(contentPath) {
    const suffix = "/content";
    if (!contentPath.endsWith(suffix)) {
        throw new Error("The artifact content path is invalid.");
    }

    const response = await fetch(`${contentPath.slice(0, -suffix.length)}/download-authorizations`, {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ range: null })
    });
    if (!response.ok) {
        throw new Error("Raw download authorization failed.");
    }

    const grant = await response.json();
    const anchor = document.createElement("a");
    anchor.href = grant.ContentUri;
    anchor.click();
}
