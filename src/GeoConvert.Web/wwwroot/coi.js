// Cross-origin-isolation shim.
//
// The multithreaded WebAssembly runtime (WasmEnableThreads) needs SharedArrayBuffer, which browsers
// only expose to a "cross-origin isolated" page — one served with these two response headers:
//   Cross-Origin-Opener-Policy:   same-origin
//   Cross-Origin-Embedder-Policy: require-corp
// GitHub Pages (where this app is hosted) can't set response headers, so a service worker re-serves
// every response with them. When the app is already isolated (e.g. a dev/test server that sets the
// headers itself), the page-side code below does nothing. This is the well-known coi-serviceworker
// technique (Guido Zuidhof, MIT), trimmed to what this app needs.

if (typeof window === 'undefined') {
    // ---- Service worker context ----
    self.addEventListener('install', () => self.skipWaiting());
    self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
    self.addEventListener('fetch', event => {
        const request = event.request;
        // A cross-origin "only-if-cached" request can't be re-issued, so leave it untouched.
        if (request.cache === 'only-if-cached' && request.mode !== 'same-origin') {
            return;
        }

        event.respondWith(
            fetch(request)
                .then(response => {
                    // Opaque (no-cors) responses have an unreadable body/headers; pass them through.
                    if (response.status === 0) {
                        return response;
                    }

                    const headers = new Headers(response.headers);
                    headers.set('Cross-Origin-Embedder-Policy', 'require-corp');
                    headers.set('Cross-Origin-Opener-Policy', 'same-origin');
                    return new Response(response.body, {
                        status: response.status,
                        statusText: response.statusText,
                        headers
                    });
                })
                .catch(error => console.error(error)));
    });
} else {
    // ---- Page context ----
    (() => {
        // Already isolated (the host set the headers), or no SW support / insecure context: nothing to do.
        if (window.crossOriginIsolated || !window.isSecureContext || !navigator.serviceWorker) {
            return;
        }

        // Guard against a reload loop if isolation still can't be reached after one reload.
        if (sessionStorage.getItem('coiReloaded')) {
            return;
        }

        navigator.serviceWorker
            .register(document.currentScript.src)
            .then(registration => {
                // First registration: the SW isn't controlling this page yet, so reload once to let it.
                if (registration.active && !navigator.serviceWorker.controller) {
                    sessionStorage.setItem('coiReloaded', '1');
                    window.location.reload();
                }

                registration.addEventListener('updatefound', () => {
                    sessionStorage.setItem('coiReloaded', '1');
                    window.location.reload();
                });
            })
            .catch(error => console.error(error));
    })();
}
