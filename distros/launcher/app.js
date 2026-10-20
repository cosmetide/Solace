(() => {
  const $ = (id) => document.getElementById(id);
  const statusPill = $("status-pill");
  const statusLabel = $("status-label");
  const stateBadge = $("state-badge");
  const statusCopy = $("status-copy");
  const heroHint = $("hero-hint");
  const startupProgress = $("startup-progress");
  const startButton = $("start-button");
  const stopButton = $("stop-button");
  const portalButton = $("portal-button");
  const portalLink = $("portal-link");
  const dashboardLink = $("dashboard-link");
  const eulaNotice = $("eula-notice");
  const packNotice = $("pack-notice");
  const factVersion = $("fact-version");
  const factBranch = $("fact-branch");
  const factMode = $("fact-mode");
  const factIp = $("fact-ip");
  const serviceList = $("service-list");
  const publicIp = $("public-ip");
  const logOutput = $("log-output");
  const toastEl = $("toast");
  let currentStatus = null;
  let toastTimer = null;
  let busy = false;

  class ApiError extends Error {
    constructor(message, payload = {}) {
      super(message);
      this.payload = payload;
    }
  }

  async function api(path, options = {}) {
    const response = await fetch(path, {
      cache: "no-store",
      headers: { "Content-Type": "application/json", ...(options.headers || {}) },
      ...options,
    });
    let data = {};
    try {
      data = await response.json();
    } catch {
      data = {};
    }
    if (!response.ok || data.ok === false) {
      throw new ApiError(data.error || `Request failed (${response.status})`, data);
    }
    return data;
  }

  function toast(message, isError = false) {
    clearTimeout(toastTimer);
    toastEl.textContent = message;
    toastEl.classList.toggle("error", isError);
    toastEl.classList.add("show");
    toastTimer = setTimeout(() => toastEl.classList.remove("show"), 5200);
  }

  function setBusy(value) {
    busy = value;
    startButton.disabled = value || !currentStatus || currentStatus.running || currentStatus.starting;
    stopButton.disabled = value || !currentStatus || currentStatus.state === "stopped";
  }

  function setState(state) {
    const normalized = ["running", "starting", "stopped", "unknown"].includes(state) ? state : "unknown";
    statusPill.classList.remove("running", "starting", "error");
    stateBadge.classList.remove("running", "starting", "error");
    if (normalized === "running") {
      statusPill.classList.add("running");
      stateBadge.classList.add("running");
      statusLabel.textContent = "Running";
      stateBadge.textContent = "Running";
    } else if (normalized === "starting") {
      statusPill.classList.add("starting");
      stateBadge.classList.add("starting");
      statusLabel.textContent = "Starting";
      stateBadge.textContent = "Starting";
    } else if (normalized === "stopped") {
      statusLabel.textContent = "Stopped";
      stateBadge.textContent = "Stopped";
    } else {
      statusPill.classList.add("error");
      stateBadge.classList.add("error");
      statusLabel.textContent = "Offline";
      stateBadge.textContent = "Unknown";
    }
    startupProgress.classList.toggle("show", normalized === "starting");
  }

  function setLink(link, url, enabled = true) {
    if (enabled && url) {
      link.href = url;
      link.classList.remove("disabled");
      link.setAttribute("aria-disabled", "false");
    } else {
      link.href = "#";
      link.classList.add("disabled");
      link.setAttribute("aria-disabled", "true");
    }
  }

  function renderServices(services) {
    serviceList.replaceChildren();
    if (!Array.isArray(services) || services.length === 0) {
      const empty = document.createElement("div");
      empty.className = "service";
      empty.textContent = "No service endpoints available.";
      serviceList.append(empty);
      return;
    }
    for (const service of services) {
      const row = document.createElement("div");
      row.className = "service";
      const name = document.createElement("strong");
      name.textContent = service.name || "Service";
      const link = document.createElement("a");
      link.href = service.url || "#";
      link.target = "_blank";
      link.rel = "noopener";
      link.textContent = service.url || "";
      row.append(name, link);
      serviceList.append(row);
    }
  }

  function renderStatus(status) {
    currentStatus = status;
    setState(status.state);
    setBusy(busy);

    factVersion.textContent = status.version || "—";
    factBranch.textContent = status.branch || "—";
    factMode.textContent = status.installMode || "—";
    factIp.textContent = status.publicIp || "—";

    portalButton.href = status.portalUrl || "#";
    portalButton.classList.toggle("disabled", !status.portalUrl);
    setLink(portalLink, status.portalUrl, Boolean(status.portalUrl));
    setLink(dashboardLink, status.dashboardUrl, Boolean(status.dashboardUrl));

    if (document.activeElement !== publicIp) {
      publicIp.value = status.publicIp || "";
    }

    eulaNotice.classList.toggle("hidden", Boolean(status.eulaAccepted));
    packNotice.classList.toggle("hidden", Boolean(status.resourcePack));

    if (status.state === "running") {
      statusCopy.textContent = status.dashboardUrl
        ? "The server is online. The Aspire dashboard and portal are ready."
        : "The server is online. The Aspire dashboard is warming up.";
      heroHint.textContent = "Launcher connected · all controls are ready.";
    } else if (status.state === "starting") {
      statusCopy.textContent = "Aspire is bringing up the stack. This can take a few minutes on the first start.";
      heroHint.textContent = "Starting Aspire · this page updates automatically.";
    } else if (status.state === "stopped") {
      statusCopy.textContent = "The server is stopped. Start it when you are ready to play.";
      heroHint.textContent = "Server offline · use Start server when you are ready.";
    } else {
      statusCopy.textContent = "The launcher could not read the backend status.";
      heroHint.textContent = "Waiting for the local Solace backend.";
    }

    renderServices(status.services);
  }

  async function refreshStatus() {
    try {
      const data = await api("/api/status");
      renderStatus(data.status);
    } catch (error) {
      currentStatus = null;
      setState("unknown");
      statusCopy.textContent = error.message;
      startButton.disabled = true;
      stopButton.disabled = true;
      statusLabel.textContent = "Offline";
    }
  }

  async function runAction(label, path, body = {}) {
    if (busy) return;
    setBusy(true);
    try {
      const data = await api(path, {
        method: "POST",
        body: JSON.stringify(body),
      });
      toast(data.output || `${label} complete.`);
      await refreshStatus();
    } catch (error) {
      toast(error.message, true);
      if (error.payload && error.payload.eulaRequired) {
        eulaNotice.classList.remove("hidden");
        eulaNotice.scrollIntoView({ behavior: "smooth", block: "center" });
      }
      await refreshStatus();
    } finally {
      setBusy(false);
    }
  }

  startButton.addEventListener("click", () => runAction("Start", "/api/start"));
  stopButton.addEventListener("click", () => {
    if (window.confirm("Stop the Solace server?")) {
      runAction("Stop", "/api/stop");
    }
  });

  $("accept-eula").addEventListener("click", () => runAction("EULA", "/api/eula", { action: "accept" }));

  $("update-button").addEventListener("click", () => {
    if (window.confirm("Update Solace now? The server will stop and the next start may rebuild.")) {
      runAction("Update", "/api/update");
    }
  });

  $("password-button").addEventListener("click", async () => {
    try {
      const data = await api("/api/admin-password");
      $("admin-password").textContent = data.password;
      $("admin-secret").classList.remove("hidden");
    } catch (error) {
      toast(error.message, true);
    }
  });

  $("ip-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    const ip = publicIp.value.trim();
    if (!ip) {
      toast("Enter an IP address or hostname.", true);
      return;
    }
    try {
      const data = await api("/api/public-ip", {
        method: "POST",
        body: JSON.stringify({ ip }),
      });
      toast(data.output || "Public endpoint saved.");
      await refreshStatus();
    } catch (error) {
      toast(error.message, true);
    }
  });

  async function refreshLogs() {
    const source = $("log-source").value;
    $("refresh-logs").disabled = true;
    logOutput.textContent = "Loading…";
    try {
      const data = await api(`/api/logs?source=${encodeURIComponent(source)}&lines=300`);
      logOutput.textContent = data.lines || "No log output yet.";
      logOutput.scrollTop = logOutput.scrollHeight;
    } catch (error) {
      logOutput.textContent = error.message;
    } finally {
      $("refresh-logs").disabled = false;
    }
  }

  $("refresh-logs").addEventListener("click", refreshLogs);
  $("log-source").addEventListener("change", refreshLogs);
  $("logs-button").addEventListener("click", () => {
    $("log-panel").scrollIntoView({ behavior: "smooth", block: "start" });
    refreshLogs();
  });

  refreshStatus();
  setInterval(refreshStatus, 4000);
})();
