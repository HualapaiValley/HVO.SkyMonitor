if (location.pathname === "/Account/Recovery" && (location.hash || location.href.endsWith("#"))) {
    history.replaceState(null, "", "/Account/Recovery");
}
