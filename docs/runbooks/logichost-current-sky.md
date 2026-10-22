# LogicHost Current Sky

Open **Network workspace**, choose an observatory and logical camera, then choose
**Current Sky**. The protected route is
`/app/network/observatories/{observatoryId}/cameras/{cameraId}/current`.
The page requires an interactive user and current observatory membership; camera
identity is resolved within that observatory, including retained installations
that have been replaced.

Current Sky uses the existing current-selection service. Each request examines at
most five captures and five eligible Preview candidates. **Search older retained
captures** explicitly continues a bounded search; the page never drains history
automatically. **Refresh Current Sky** starts again at the latest capture.

Captured and first-received times are independent. A retained presentation does
not establish current fleet health. A plain Preview needs no optional annotation.
Layered content must match both the selected capture and its selected base artifact.
Unavailable content retains its metadata and reasons without substituting an image.
If an image request fails, **Retry image** requests that same image; refresh
rechecks the selection and access.

The page revalidates every 30 seconds and when the browser regains focus, comes
online, becomes visible, or reconnects its server circuit. The previous display is
cleared while revalidation is pending. Authorization and storage failures clear
protected content. Existing protected content endpoints reauthorize requests;
Current Sky introduces no shared image cache or saved selection policy.
