(() => {
  const $ = (id) => document.getElementById(id);

  const elements = {
    serverState: $("server-state"),
    stateLabel: $("state-label"),
    stateNote: $("state-note"),
    start: $("start-button"),
    restart: $("restart-button"),
    stop: $("stop-button"),
    dashboard: $("dashboard-button"),
    portal: $("portal-button"),
    eulaNotice: $("eula-notice"),
    acceptEula: $("accept-eula-button"),
    runtimeBadge: $("runtime-badge"),
    factVersion: $("fact-version"),
    factBranch: $("fact-branch"),
    factMode: $("fact-mode"),
    factIp: $("fact-ip"),
    factPort: $("fact-port"),
    factPack: $("fact-pack"),
    statusCopy: $("status-copy"),
    adminPassword: $("admin-password"),
    passwordButton: $("password-button"),
    packBadge: $("pack-badge"),
    packDrop: $("pack-drop"),
    packInput: $("pack-input"),
    packTitle: $("pack-title"),
    packNote: $("pack-note"),
    packButton: $("pack-button"),
    packStatus: $("pack-status"),
    breadcrumb: $("breadcrumb"),
    fileInput: $("file-input"),
    uploadButton: $("upload-button"),
    newFolderButton: $("new-folder-button"),
    showHidden: $("show-hidden"),
    fileList: $("file-list"),
    filesRefresh: $("files-refresh"),
    logSource: $("log-source"),
    logLines: $("log-lines"),
    logsRefresh: $("logs-refresh"),
    logOutput: $("log-output"),
    processesRefresh: $("processes-refresh"),
    processOutput: $("process-output"),
    ipForm: $("ip-form"),
    publicIp: $("public-ip"),
    branchForm: $("branch-form"),
    branchName: $("branch-name"),
    eulaState: $("eula-state"),
    settingsAcceptEula: $("settings-accept-eula"),
    revokeEula: $("revoke-eula"),
    updateButton: $("update-button"),
    resetButton: $("reset-button"),
    aboutLine: $("about-line"),
    editorModal: $("editor-modal"),
    editorTitle: $("editor-title"),
    editorText: $("editor-text"),
    editorClose: $("editor-close"),
    editorCancel: $("editor-cancel"),
    editorSave: $("editor-save"),
    toast: $("toast"),
  };

  const state = {
    status: null,
    busy: false,
    filesPath: "",
    hidden: false,
    files: [],
    editingPath: null,
    originalText: "",
    toastTimer: null,
    statusInFlight: false,
  };

  class ApiError extends Error {
    constructor(message, payload = {}) {
      super(message);
      this.payload = payload;
    }
  }

  async function api(path, options = {}) {
    const headers = { ...(options.headers || {}) };
    if (options.body !== undefined && typeof options.body === "string" && !headers["Content-Type"]) {
      headers["Content-Type"] = "application/json";
    }
    const response = await fetch(path, {
      cache: "no-store",
      ...options,
      headers,
    });
    let data = {};
    try {
      data = await response.json();
    } catch (error) {
      data = {};
    }
    if (!response.ok || data.ok === false) {
      throw new ApiError(data.error || `Request failed (${response.status})`, data);
    }
    return data;
  }

  function toast(message, isError = false) {
    clearTimeout(state.toastTimer);
    elements.toast.textContent = message;
    elements.toast.classList.toggle("error", isError);
    elements.toast.classList.add("show");
    state.toastTimer = setTimeout(() => elements.toast.classList.remove("show"), 5200);
  }

  function formatBytes(value) {
    if (!Number.isFinite(value) || value < 0) return "-";
    if (value < 1024) return `${value} B`;
    const units = ["KB", "MB", "GB", "TB"];
    let size = value / 1024;
    let index = 0;
    while (size >= 1024 && index < units.length - 1) {
      size /= 1024;
      index += 1;
    }
    return `${size.toFixed(size >= 10 ? 0 : 1)} ${units[index]}`;
  }

  function formatDate(value) {
    if (!value) return "-";
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "-";
    return date.toLocaleString();
  }

  function setState(value) {
    const normalized = ["running", "starting", "stopped", "unknown"].includes(value) ? value : "unknown";
    elements.serverState.classList.remove("running", "starting", "error");
    if (normalized === "running") {
      elements.serverState.classList.add("running");
      elements.stateLabel.textContent = "Server online";
      elements.stateNote.textContent = "All Solace services are available.";
    } else if (normalized === "starting") {
      elements.serverState.classList.add("starting");
      elements.stateLabel.textContent = "Server starting";
      elements.stateNote.textContent = "Aspire is bringing the service stack online.";
    } else if (normalized === "stopped") {
      elements.stateLabel.textContent = "Server offline";
      elements.stateNote.textContent = "Use Start Server when you are ready.";
    } else {
      elements.serverState.classList.add("error");
      elements.stateLabel.textContent = "Status unavailable";
      elements.stateNote.textContent = "The local backend did not respond.";
    }
    renderButtons();
  }

  function renderButtons() {
    const current = state.status;
    const serverState = current && current.state ? current.state : "unknown";
    const running = serverState === "running";
    const starting = serverState === "starting";

    elements.start.disabled = state.busy || !current || running || starting;
    elements.restart.disabled = state.busy || !current || !running;
    elements.stop.disabled = state.busy || !current || (!running && !starting);

    elements.runtimeBadge.classList.remove("running", "starting", "error");
    if (running) {
      elements.runtimeBadge.classList.add("running");
      elements.runtimeBadge.textContent = "Running";
    } else if (starting) {
      elements.runtimeBadge.classList.add("starting");
      elements.runtimeBadge.textContent = "Starting";
    } else if (serverState === "unknown") {
      elements.runtimeBadge.classList.add("error");
      elements.runtimeBadge.textContent = "Unknown";
    } else {
      elements.runtimeBadge.textContent = "Stopped";
    }
  }

  function setPanelLink(link, url, enabled) {
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

  function updatePack(status) {
    const installed = Boolean(status.resourcePack);
    elements.packBadge.textContent = installed ? "Installed" : "Missing";
    elements.packBadge.classList.toggle("running", installed);
    elements.packTitle.textContent = installed ? "vanilla.zip is installed" : "Drop vanilla.zip here";
    if (installed) {
      elements.packNote.textContent = `${formatBytes(status.resourcePackSize)} saved to staticdata/resourcepacks/vanilla.zip. Drop another archive to replace it.`;
    } else {
      elements.packNote.textContent = "Or choose the file from this device. It is saved directly to staticdata/resourcepacks/vanilla.zip inside Solace.";
    }
    elements.factPack.textContent = installed ? formatBytes(status.resourcePackSize) : "Not installed";
  }

  function renderStatus(status) {
    state.status = status;
    setState(status.state);
    renderButtons();

    elements.factVersion.textContent = status.version || "Not available";
    elements.factBranch.textContent = status.branch || "Not available";
    elements.factMode.textContent = status.installMode || "Not available";
    elements.factIp.textContent = status.publicIp || "Not available";
    elements.factPort.textContent = status.launcherPort ? String(status.launcherPort) : "7089";
    elements.aboutLine.textContent = `Local control panel on port ${status.launcherPort || 7089}.`;

    if (document.activeElement !== elements.publicIp) {
      elements.publicIp.value = status.publicIp || "";
    }
    if (document.activeElement !== elements.branchName) {
      elements.branchName.value = status.branch || "";
    }

    const running = status.state === "running";
    setPanelLink(elements.dashboard, status.dashboardUrl, running && Boolean(status.dashboardUrl));
    setPanelLink(elements.portal, status.portalUrl, running && Boolean(status.portalUrl));

    elements.eulaNotice.classList.toggle("hidden", Boolean(status.eulaAccepted));
    elements.eulaState.textContent = status.eulaAccepted
      ? "The Minecraft EULA has been accepted."
      : "The Minecraft EULA has not been accepted. Start is disabled until it is accepted.";
    elements.settingsAcceptEula.disabled = Boolean(status.eulaAccepted);
    elements.revokeEula.disabled = !status.eulaAccepted;

    if (status.state === "running") {
      elements.statusCopy.textContent = status.dashboardUrl
        ? "The server is online. Open the Aspire panel for resource details, or the web panel for the player portal."
        : "The server is online. The Aspire panel is still warming up; the web portal is available.";
    } else if (status.state === "starting") {
      elements.statusCopy.textContent = "Aspire is starting the service stack. The first start can take several minutes on a phone.";
    } else if (status.state === "stopped") {
      elements.statusCopy.textContent = "The server is stopped. Use Start Server to bring the primary services online.";
    } else {
      elements.statusCopy.textContent = "The launcher could not read the local Solace status.";
    }

    updatePack(status);

    if (status.adminEmail) {
      const account = elements.adminPassword.parentElement
        ? elements.adminPassword.parentElement.querySelector("span")
        : null;
      if (account) account.textContent = status.adminEmail;
    }
  }

  async function refreshStatus() {
    if (state.statusInFlight) return;
    state.statusInFlight = true;
    try {
      const data = await api("/api/status");
      renderStatus(data.status);
    } catch (error) {
      state.status = { state: "unknown" };
      setState("unknown");
      elements.factVersion.textContent = "Not available";
      elements.factBranch.textContent = "Not available";
      elements.factMode.textContent = "Not available";
      elements.factIp.textContent = "Not available";
      elements.statusCopy.textContent = error.message;
      elements.runtimeBadge.classList.add("error");
      elements.runtimeBadge.textContent = "Unknown";
      elements.start.disabled = true;
      elements.restart.disabled = true;
      elements.stop.disabled = true;
      setPanelLink(elements.dashboard, "", false);
      setPanelLink(elements.portal, "", false);
    } finally {
      state.statusInFlight = false;
    }
  }

  async function runAction(label, path, body = {}) {
    if (state.busy) return false;
    state.busy = true;
    renderButtons();
    try {
      const data = await api(path, { method: "POST", body: JSON.stringify(body) });
      toast(data.output || `${label} completed.`);
      await refreshStatus();
      return true;
    } catch (error) {
      toast(error.message, true);
      if (error.payload && error.payload.eulaRequired) {
        elements.eulaNotice.classList.remove("hidden");
        elements.eulaNotice.scrollIntoView({ behavior: "smooth", block: "center" });
      }
      await refreshStatus();
      return false;
    } finally {
      state.busy = false;
      renderButtons();
    }
  }

  async function uploadFile(path, file, contentType, onProgress) {
    const response = await fetch(path, {
      method: "PUT",
      cache: "no-store",
      headers: { "Content-Type": contentType || "application/octet-stream" },
      body: file,
    });
    let data = {};
    try {
      data = await response.json();
    } catch (error) {
      data = {};
    }
    if (!response.ok || data.ok === false) {
      throw new ApiError(data.error || `Upload failed (${response.status})`, data);
    }
    if (onProgress) onProgress(data);
    return data;
  }

  function activateTab(name) {
    document.querySelectorAll(".tab").forEach((tab) => {
      tab.classList.toggle("active", tab.dataset.tab === name);
    });
    document.querySelectorAll(".tab-panel").forEach((panel) => {
      panel.classList.toggle("active", panel.id === `panel-${name}`);
    });
    if (name === "files") {
      loadFiles(state.filesPath);
    }
    if (name === "logs") {
      refreshLogs();
      refreshProcesses();
    }
  }

  async function loadFiles(path = state.filesPath) {
    elements.fileList.replaceChildren();
    const loading = document.createElement("tr");
    loading.className = "empty-row";
    const cell = document.createElement("td");
    cell.colSpan = 4;
    cell.textContent = "Loading files...";
    loading.append(cell);
    elements.fileList.append(loading);

    try {
      const data = await api(
        `/api/files?path=${encodeURIComponent(path)}&hidden=${state.hidden ? "1" : "0"}`,
      );
      state.filesPath = data.path || "";
      state.files = data.entries || [];
      renderBreadcrumb(state.filesPath);
      renderFiles(data);
    } catch (error) {
      elements.fileList.replaceChildren();
      const row = document.createElement("tr");
      row.className = "empty-row";
      const cell = document.createElement("td");
      cell.colSpan = 4;
      cell.textContent = error.message;
      row.append(cell);
      elements.fileList.append(row);
      toast(error.message, true);
    }
  }

  function renderBreadcrumb(path) {
    elements.breadcrumb.replaceChildren();
    const parts = path ? path.split("/") : [];
    const root = document.createElement("button");
    root.type = "button";
    root.className = "crumb";
    root.textContent = "Solace";
    root.addEventListener("click", () => loadFiles(""));
    elements.breadcrumb.append(root);

    let current = "";
    parts.forEach((part) => {
      const separator = document.createElement("span");
      separator.className = "crumb-separator";
      separator.textContent = "/";
      elements.breadcrumb.append(separator);

      current = current ? `${current}/${part}` : part;
      const button = document.createElement("button");
      button.type = "button";
      button.className = "crumb";
      button.textContent = part;
      button.addEventListener("click", () => loadFiles(current));
      elements.breadcrumb.append(button);
    });

    const crumbs = elements.breadcrumb.querySelectorAll(".crumb");
    if (crumbs.length) {
      crumbs[crumbs.length - 1].classList.add("current");
    }
  }

  function appendCell(row, text, className = "") {
    const cell = document.createElement("td");
    if (className) cell.className = className;
    cell.textContent = text;
    row.append(cell);
    return cell;
  }

  function fileAction(label, handler, danger = false) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `mini-button${danger ? " danger" : ""}`;
    button.textContent = label;
    button.addEventListener("click", handler);
    return button;
  }

  function renderFiles(data) {
    elements.fileList.replaceChildren();
    const parent = data.parent;

    const parentRow = document.createElement("tr");
    const parentCell = document.createElement("td");
    const parentButton = document.createElement("button");
    parentButton.type = "button";
    parentButton.className = "file-name parent";
    parentButton.textContent = "..";
    parentButton.disabled = parent === null || parent === undefined;
    parentButton.title = parent === null ? "Already at the Solace folder root" : "Go up one folder";
    parentButton.addEventListener("click", () => {
      if (parent !== null && parent !== undefined) loadFiles(parent);
    });
    parentCell.append(parentButton);
    parentRow.append(parentCell);
    appendCell(parentRow, "-");
    appendCell(parentRow, "-");
    appendCell(parentRow, "");
    elements.fileList.append(parentRow);

    if (!data.entries || data.entries.length === 0) {
      const row = document.createElement("tr");
      row.className = "empty-row";
      const cell = document.createElement("td");
      cell.colSpan = 4;
      cell.textContent = "This folder is empty.";
      row.append(cell);
      elements.fileList.append(row);
      return;
    }

    data.entries.forEach((entry) => {
      const row = document.createElement("tr");
      const nameCell = document.createElement("td");
      const nameButton = document.createElement("button");
      nameButton.type = "button";
      nameButton.className = `file-name${entry.type === "dir" ? " dir" : ""}`;
      nameButton.textContent = entry.name;
      if (entry.type === "dir") {
        nameButton.title = "Open folder";
        nameButton.addEventListener("click", () => {
          loadFiles(state.filesPath ? `${state.filesPath}/${entry.name}` : entry.name);
        });
      } else {
        nameButton.title = entry.type === "link" ? "Symbolic link" : "File";
        nameButton.disabled = true;
      }
      nameCell.append(nameButton);
      row.append(nameCell);

      appendCell(row, entry.type === "dir" ? "-" : formatBytes(entry.size));
      appendCell(row, formatDate(entry.modified));

      const actions = document.createElement("td");
      actions.className = "file-actions";
      if (entry.type === "dir") {
        actions.append(
          fileAction("Open", () => {
            loadFiles(state.filesPath ? `${state.filesPath}/${entry.name}` : entry.name);
          }),
        );
      } else {
        if (entry.editable) {
          actions.append(fileAction("Edit", () => openEditor(entry.name)));
        }
        actions.append(fileAction("Download", () => downloadFile(entry.name)));
      }
      actions.append(
        fileAction(
          "Delete",
          () => deletePath(entry.name, entry.type === "dir"),
          true,
        ),
      );
      row.append(actions);
      elements.fileList.append(row);
    });
  }

  function currentFilePath(name) {
    return state.filesPath ? `${state.filesPath}/${name}` : name;
  }

  function downloadFile(name) {
    const link = document.createElement("a");
    link.href = `/api/files/download?path=${encodeURIComponent(currentFilePath(name))}`;
    link.download = name;
    document.body.append(link);
    link.click();
    link.remove();
  }

  async function deletePath(name, isDirectory) {
    const target = currentFilePath(name);
    const warning = isDirectory
      ? `Delete the folder "${name}" and everything inside it?`
      : `Delete the file "${name}"?`;
    if (!window.confirm(warning)) return;
    try {
      await api(`/api/files?path=${encodeURIComponent(target)}`, { method: "DELETE" });
      toast(`${name} deleted.`);
      await loadFiles(state.filesPath);
    } catch (error) {
      toast(error.message, true);
    }
  }

  async function uploadCurrentFile(file) {
    if (!file) return;
    const target = currentFilePath(file.name);
    const existing = state.files.some((entry) => entry.name === file.name);
    if (existing && !window.confirm(`Replace the existing "${file.name}"?`)) {
      elements.fileInput.value = "";
      return;
    }
    elements.uploadButton.disabled = true;
    elements.uploadButton.textContent = "Uploading...";
    try {
      await uploadFile(
        `/api/files/write?path=${encodeURIComponent(target)}`,
        file,
        "application/octet-stream",
      );
      toast(`${file.name} uploaded.`);
      await loadFiles(state.filesPath);
    } catch (error) {
      toast(error.message, true);
    } finally {
      elements.uploadButton.disabled = false;
      elements.uploadButton.textContent = "Upload File";
      elements.fileInput.value = "";
    }
  }

  async function openEditor(name) {
    const path = currentFilePath(name);
    try {
      const data = await api(`/api/files/read?path=${encodeURIComponent(path)}`);
      state.editingPath = data.path;
      state.originalText = data.text;
      elements.editorTitle.textContent = data.path;
      elements.editorText.value = data.text;
      elements.editorModal.classList.remove("hidden");
      elements.editorText.focus();
    } catch (error) {
      toast(error.message, true);
    }
  }

  function closeEditor(force = false) {
    if (!force && elements.editorText.value !== state.originalText) {
      if (!window.confirm("Discard unsaved changes?")) return;
    }
    elements.editorModal.classList.add("hidden");
    state.editingPath = null;
    state.originalText = "";
  }

  async function saveEditor() {
    if (!state.editingPath) return;
    elements.editorSave.disabled = true;
    elements.editorSave.textContent = "Saving...";
    try {
      await uploadFile(
        `/api/files/write?path=${encodeURIComponent(state.editingPath)}`,
        new Blob([elements.editorText.value], { type: "text/plain" }),
        "text/plain;charset=UTF-8",
      );
      state.originalText = elements.editorText.value;
      elements.editorModal.classList.add("hidden");
      state.editingPath = null;
      toast("File saved.");
      await loadFiles(state.filesPath);
    } catch (error) {
      toast(error.message, true);
    } finally {
      elements.editorSave.disabled = false;
      elements.editorSave.textContent = "Save File";
    }
  }

  async function uploadResourcePack(file) {
    if (!file) return;
    if (!file.name.toLowerCase().endsWith(".zip")) {
      toast("Choose the vanilla.zip archive.", true);
      return;
    }
    elements.packButton.disabled = true;
    elements.packButton.textContent = "Uploading...";
    elements.packStatus.textContent = `Uploading ${file.name} (${formatBytes(file.size)})...`;
    try {
      const data = await uploadFile("/api/resourcepack", file, "application/zip");
      elements.packStatus.textContent = `Upload complete: ${formatBytes(data.size)}.`;
      toast("vanilla.zip uploaded.");
      await refreshStatus();
    } catch (error) {
      elements.packStatus.textContent = "";
      toast(error.message, true);
    } finally {
      elements.packButton.disabled = false;
      elements.packButton.textContent = "Choose vanilla.zip";
      elements.packInput.value = "";
    }
  }

  async function refreshLogs() {
    elements.logsRefresh.disabled = true;
    elements.logOutput.textContent = "Loading log...";
    try {
      const source = elements.logSource.value;
      const lines = elements.logLines.value;
      const data = await api(
        `/api/logs?source=${encodeURIComponent(source)}&lines=${encodeURIComponent(lines)}`,
      );
      elements.logOutput.textContent = data.lines || "No log output yet.";
      elements.logOutput.scrollTop = elements.logOutput.scrollHeight;
    } catch (error) {
      elements.logOutput.textContent = error.message;
    } finally {
      elements.logsRefresh.disabled = false;
    }
  }

  async function refreshProcesses() {
    elements.processesRefresh.disabled = true;
    elements.processOutput.textContent = "Reading processes...";
    try {
      const data = await api("/api/processes");
      elements.processOutput.textContent = data.processes || "No Solace processes are running.";
    } catch (error) {
      elements.processOutput.textContent = error.message;
    } finally {
      elements.processesRefresh.disabled = false;
    }
  }

  async function revealPassword() {
    elements.passwordButton.disabled = true;
    elements.passwordButton.textContent = "Reading...";
    try {
      const data = await api("/api/admin-password");
      elements.adminPassword.textContent = data.password;
      toast("Admin password loaded.");
    } catch (error) {
      toast(error.message, true);
    } finally {
      elements.passwordButton.disabled = false;
      elements.passwordButton.textContent = "Reveal Password";
    }
  }

  elements.start.addEventListener("click", () => runAction("Start", "/api/start"));
  elements.restart.addEventListener("click", () => {
    if (window.confirm("Restart the Solace server?")) {
      runAction("Restart", "/api/restart");
    }
  });
  elements.stop.addEventListener("click", () => {
    if (window.confirm("Stop the Solace server?")) {
      runAction("Stop", "/api/stop");
    }
  });

  elements.dashboard.addEventListener("click", (event) => {
    if (elements.dashboard.classList.contains("disabled")) event.preventDefault();
  });
  elements.portal.addEventListener("click", (event) => {
    if (elements.portal.classList.contains("disabled")) event.preventDefault();
  });

  elements.acceptEula.addEventListener("click", () => runAction("EULA", "/api/eula", { action: "accept" }));
  elements.settingsAcceptEula.addEventListener("click", () =>
    runAction("EULA", "/api/eula", { action: "accept" }),
  );
  elements.revokeEula.addEventListener("click", () => {
    if (window.confirm("Revoke the Minecraft EULA acceptance? The server will no longer start.")) {
      runAction("EULA", "/api/eula", { action: "delete" });
    }
  });

  elements.passwordButton.addEventListener("click", revealPassword);

  elements.ipForm.addEventListener("submit", async (event) => {
    event.preventDefault();
    const ip = elements.publicIp.value.trim();
    if (!ip) {
      toast("Enter an IP address or hostname.", true);
      return;
    }
    const button = elements.ipForm.querySelector("button");
    button.disabled = true;
    try {
      const data = await api("/api/public-ip", {
        method: "POST",
        body: JSON.stringify({ ip }),
      });
      toast(data.output || "Public endpoint saved.");
      await refreshStatus();
    } catch (error) {
      toast(error.message, true);
    } finally {
      button.disabled = false;
    }
  });

  elements.branchForm.addEventListener("submit", async (event) => {
    event.preventDefault();
    const branch = elements.branchName.value.trim();
    if (!branch) {
      toast("Enter a branch name.", true);
      return;
    }
    if (!window.confirm(`Switch to "${branch}"? The server will stop and the checkout will update.`)) {
      return;
    }
    await runAction("Branch switch", "/api/branch", { branch });
  });

  elements.updateButton.addEventListener("click", () => {
    if (window.confirm("Update Solace now? The server will stop and the next start may rebuild.")) {
      runAction("Update", "/api/update");
    }
  });

  elements.resetButton.addEventListener("click", async () => {
    const confirmation = window.prompt('This permanently deletes Solace databases.\nType RESET to continue.');
    if (confirmation !== "RESET") {
      if (confirmation !== null) toast("Database reset cancelled.", true);
      return;
    }
    await runAction("Database reset", "/api/reset-databases", { confirm: "RESET" });
  });

  document.querySelectorAll(".tab").forEach((tab) => {
    tab.addEventListener("click", () => activateTab(tab.dataset.tab));
  });

  elements.filesRefresh.addEventListener("click", () => loadFiles(state.filesPath));
  elements.uploadButton.addEventListener("click", () => elements.fileInput.click());
  elements.fileInput.addEventListener("change", () => uploadCurrentFile(elements.fileInput.files[0]));
  elements.showHidden.addEventListener("change", () => {
    state.hidden = elements.showHidden.checked;
    loadFiles(state.filesPath);
  });

  elements.newFolderButton.addEventListener("click", async () => {
    const name = window.prompt("New folder name:");
    if (!name || !name.trim()) return;
    const target = currentFilePath(name.trim());
    try {
      await api("/api/files/mkdir", {
        method: "POST",
        body: JSON.stringify({ path: target }),
      });
      toast("Folder created.");
      await loadFiles(state.filesPath);
    } catch (error) {
      toast(error.message, true);
    }
  });

  elements.packButton.addEventListener("click", () => elements.packInput.click());
  elements.packInput.addEventListener("change", () => uploadResourcePack(elements.packInput.files[0]));

  ["dragenter", "dragover"].forEach((eventName) => {
    elements.packDrop.addEventListener(eventName, (event) => {
      event.preventDefault();
      elements.packDrop.classList.add("dragging");
    });
  });
  ["dragleave", "drop"].forEach((eventName) => {
    elements.packDrop.addEventListener(eventName, (event) => {
      event.preventDefault();
      elements.packDrop.classList.remove("dragging");
    });
  });
  elements.packDrop.addEventListener("drop", (event) => {
    const file = event.dataTransfer.files[0];
    if (file) uploadResourcePack(file);
  });

  elements.logsRefresh.addEventListener("click", refreshLogs);
  elements.logSource.addEventListener("change", refreshLogs);
  elements.logLines.addEventListener("change", refreshLogs);
  elements.processesRefresh.addEventListener("click", refreshProcesses);

  elements.editorClose.addEventListener("click", () => closeEditor());
  elements.editorCancel.addEventListener("click", () => closeEditor());
  elements.editorSave.addEventListener("click", saveEditor);
  elements.editorModal.addEventListener("click", (event) => {
    if (event.target === elements.editorModal) closeEditor();
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && !elements.editorModal.classList.contains("hidden")) {
      closeEditor();
    }
    if (
      (event.ctrlKey || event.metaKey) &&
      event.key.toLowerCase() === "s" &&
      !elements.editorModal.classList.contains("hidden")
    ) {
      event.preventDefault();
      saveEditor();
    }
  });

  activateTab("overview");
  refreshStatus();
  setInterval(refreshStatus, 5000);
})();
