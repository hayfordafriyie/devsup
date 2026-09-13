(function () {
    "use strict";

    var token = localStorage.getItem("devsup.token") || "";
    var tokenInput = document.getElementById("token");
    var connectBtn = document.getElementById("connect");
    var refreshBtn = document.getElementById("refresh");
    var sessionUser = document.getElementById("session-user");
    var errorEl = document.getElementById("error");

    function showError(message) {
        errorEl.textContent = message;
        errorEl.hidden = false;
        window.clearTimeout(showError._timer);
        showError._timer = window.setTimeout(function () { errorEl.hidden = true; }, 6000);
    }

    function badge(status) {
        var classes = { ok: "ok", warning: "warn", bad: "bad", muted: "muted" };
        var kind = classes[status.kind] || classes.muted;
        return '<span class="status ' + kind + '">' + escapeHtml(status.label) + "</span>";
    }

    function escapeHtml(text) {
        return String(text).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }

    function repoName(cloneUrl) {
        var trimmed = String(cloneUrl).replace(/\.git$/, "").replace(/\/$/, "");
        return trimmed.split("/").slice(-1)[0];
    }

    function healthStatus(row) {
        if (row.appHealthy === true) return { label: "Healthy", kind: "ok" };
        if (row.appHealthy === false) return { label: "Unhealthy", kind: "bad" };
        return { label: "Unchecked", kind: "muted" };
    }

    function ticketStatus(status) {
        switch (status) {
            case "new": return { label: "New", kind: "warn" };
            case "triaged":
            case "investigating":
            case "patchProposed": return { label: "In progress", kind: "warn" };
            case "fixPendingReview": return { label: "Pending review", kind: "warn" };
            case "fixPushed":
            case "fixVerified":
            case "closed": return { label: "Fixed", kind: "ok" };
            case "needsHumanReview": return { label: "Needs review", kind: "bad" };
            default: return { label: status, kind: "muted" };
        }
    }

    async function api(path) {
        var response = await fetch(path, {
            headers: token ? { Authorization: "Bearer " + token } : {}
        });
        if (response.status === 401 || response.status === 403) {
            sessionUser.textContent = "";
            throw new Error("The token is invalid or expired. Reconnect.");
        }
        if (!response.ok) {
            var body = await response.json().catch(function () { return null; });
            var message = body && body.message ? body.message : (response.status + " " + response.statusText);
            throw new Error(message);
        }
        return response.json();
    }

    var isAdmin = false;

    async function adminApi(path) {
        var response = await fetch(path, {
            headers: token ? { Authorization: "Bearer " + token } : {}
        });
        if (response.status === 403) {
            return null;
        }
        if (!response.ok) {
            var body = await response.json().catch(function () { return null; });
            throw new Error(body && body.message ? body.message : (response.status + " " + response.statusText));
        }
        return response.json();
    }

    function renderCards(overview) {
        var cards = document.getElementById("cards");
        var counts = overview.tickets;
        var buckets = [
            { label: "Repositories", value: overview.repositoryCount },
            { label: "Healthy", value: overview.healthyRepos },
            { label: "Unhealthy", value: overview.unhealthyRepos },
            { label: "Unchecked", value: overview.uncheckedRepos },
            { label: "Open tickets", value: counts.new + counts.inProgress + counts.pendingReview + counts.needsHumanReview },
            { label: "Fixed", value: counts.fixed }
        ];
        cards.innerHTML = buckets.map(function (b) {
            return '<div class="card"><div class="label">' + b.label + '</div><div class="value">' + b.value + "</div></div>";
        }).join("");
        cards.hidden = false;
    }

    function renderRepositories(overview) {
        var section = document.getElementById("repositories-section");
        var tbody = section.querySelector("tbody");
        if (overview.repositories.length === 0) {
            section.hidden = true;
            return;
        }
        tbody.innerHTML = overview.repositories.map(function (row) {
            var status = healthStatus(row);
            var checked = row.appHealthCheckedAt
                ? new Date(row.appHealthCheckedAt).toLocaleString()
                : "never";
            return "<tr>" +
                "<td>" + escapeHtml(repoName(row.cloneUrl)) + "</td>" +
                "<td><a href=\"" + escapeHtml(row.appUrl || "#") + "\" target=\"_blank\" rel=\"noopener\">" + escapeHtml(row.appUrl || "not configured") + "</a></td>" +
                "<td>" + badge(status) + "</td>" +
                "<td>" + escaped(checked) + "</td>" +
                "<td>" + escaped(row.appHealthLastError) + "</td>" +
                "</tr>";
        }).join("");
        section.hidden = false;
    }

    function escaped(value) {
        return value === null || value === undefined || value === "" ? '<span class="muted">&mdash;</span>' : escapeHtml(value);
    }

    function renderTickets(tickets) {
        var section = document.getElementById("tickets-section");
        var tbody = section.querySelector("tbody");
        if (tickets.length === 0) {
            section.hidden = true;
            return;
        }
        tbody.innerHTML = tickets.map(function (t) {
            var status = ticketStatus(t.status);
            var patch = t.patchSummary
                ? '<a href="#" title="' + escapeHtml(t.patchSummary) + '">' + escapeHtml(t.patchSummary) + "</a>"
                : '<span class="muted">&mdash;</span>';
            if (t.pullRequestUrl) {
                patch += ' &middot; <a href="' + escapeHtml(t.pullRequestUrl) + '" target="_blank" rel="noopener">PR</a>';
            }
            return "<tr>" +
                "<td>" + escaped(t.repositoryId) + "</td>" +
                "<td>" + escaped((t.method || "") + " " + (t.path || "")) + "</td>" +
                "<td>" + badge(status) + "</td>" +
                "<td>" + escaped(t.category + " \u00b7 " + t.kind) + "</td>" +
                "<td>" + patch + "</td>" +
                "</tr>";
        }).join("");
        section.hidden = false;
    }

    function channelBadge(channel) {
        var labels = { http: "HTTP", slack: "Slack", teams: "Teams" };
        return '<span class="channel">' + (labels[channel] || escapeHtml(channel)) + "</span>";
    }

    function renderWebhooks(webhooks) {
        var section = document.getElementById("webhooks-section");
        var list = document.getElementById("webhooks");
        if (webhooks.length === 0) {
            list.innerHTML = '<li class="muted">No webhook endpoints.</li>';
        } else {
            list.innerHTML = webhooks.map(function (w) {
                var name = w.name ? '<strong>' + escapeHtml(w.name) + "</strong>" : "";
                var events = escapeHtml(w.events.map(function (e) {
                    return String(e).replace(/([A-Z])/g, " $1").toLowerCase();
                }).join(", "));
                return "<li>" + channelBadge(w.channel) + " " + name +
                    ' <a href="' + escapeHtml(w.url) + '" target="_blank" rel="noopener">' + escapeHtml(w.url) + "</a>" +
                    '<span class="muted">&nbsp;&middot; ' + events + "</span>" +
                    '<button data-delete="' + w.id + '">Delete</button></li>';
            }).join("");
        }
        section.hidden = false;
    }

    function renderAdminUsers(users) {
        var tbody = document.getElementById("admin-users").querySelector("tbody");
        tbody.innerHTML = users.map(function (u) {
            var status = u.active ? '<span class="status ok">Active</span>' : '<span class="status bad">Suspended</span>';
            var action = u.active
                ? '<button data-suspend="' + u.id + '">Suspend</button>'
                : '<button data-restore="' + u.id + '">Restore</button>';
            return "<tr>" +
                "<td>" + escapeHtml(u.email) + "</td>" +
                "<td>" + escapeHtml(u.displayName) + "</td>" +
                "<td>" + (u.isAdmin ? '<span class="status warn">Admin</span>' : '<span class="muted">Member</span>') + "</td>" +
                "<td>" + status + "</td>" +
                "<td>" + u.repositoryCount + "</td>" +
                "<td>" + u.ticketCount + "</td>" +
                "<td>" + action + "</td>" +
                "</tr>";
        }).join("");
    }

    function renderAdminAudit(entries) {
        var list = document.getElementById("admin-audit");
        if (entries.length === 0) {
            list.innerHTML = '<li class="muted">No audit entries.</li>';
            return;
        }
        list.innerHTML = entries.slice(0, 100).map(function (e) {
            return "<li><span class=\"muted\">" + escapeHtml(new Date(e.timestamp).toLocaleString()) + "</span> " +
                "<strong>" + escapeHtml(e.actorEmail) + "</strong> " + escapeHtml(e.action) +
                ' <span class="muted">' + escapeHtml(e.entityType) + "</span>" +
                (e.after ? ' &rarr; <em>' + escapeHtml(e.after) + "</em>" : "") + "</li>";
        }).join("");
    }

    function renderAdminFailures(page) {
        var tbody = document.getElementById("admin-failures").querySelector("tbody");
        if (page.items.length === 0) {
            tbody.innerHTML = "<tr><td colspan=\"5\" class=\"muted\">No failures recorded.</td></tr>";
            return;
        }
        tbody.innerHTML = page.items.map(function (f) {
            return "<tr>" +
                "<td>" + escaped(new Date(f.occurredAt).toLocaleString()) + "</td>" +
                "<td>" + escaped((f.method || "") + " " + (f.path || "")) + "</td>" +
                "<td>" + f.statusCode + "</td>" +
                "<td>" + escaped(f.ticketStatus || "—") + "</td>" +
                "<td>" + escaped(f.patchSummary || "—") + "</td>" +
                "</tr>";
        }).join("");
    }

    async function loadAdmin() {
        var users = await adminApi("/api/admin/users");
        if (users === null) {
            isAdmin = false;
            document.getElementById("admin-toggle").hidden = true;
            document.getElementById("admin-section").hidden = true;
            return;
        }
        isAdmin = true;
        document.getElementById("admin-toggle").hidden = false;
        if (document.getElementById("admin-section").hidden) return;
        renderAdminUsers(users);
        var audit = await adminApi("/api/admin/audit");
        renderAdminAudit(audit || []);
        var failures = await adminApi("/api/failures?pageSize=50");
        renderAdminFailures(failures || { items: [] });
    }

    async function exportCsv() {
        var response = await fetch("/api/failures/export", {
            headers: { Authorization: "Bearer " + token }
        });
        if (!response.ok) throw new Error("Failed to export CSV");
        var blob = await response.blob();
        var url = URL.createObjectURL(blob);
        var link = document.createElement("a");
        link.href = url;
        link.download = "devsup-failures.csv";
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    }

    async function load() {
        var overview = await api("/api/overview");
        var tickets = await api("/api/tickets");
        var webhooks = await api("/api/webhooks");
        renderCards(overview);
        renderRepositories(overview);
        renderTickets(tickets);
        renderWebhooks(webhooks);
        await loadAdmin();
    }

    function showSession() {
        sessionUser.textContent = token ? "Connected" : "";
        tokenInput.hidden = true;
        connectBtn.hidden = true;
        refreshBtn.hidden = false;
    }

    connectBtn.addEventListener("click", function () {
        var value = tokenInput.value.trim();
        if (!value) return;
        token = value;
        localStorage.setItem("devsup.token", token);
        showSession();
        load().catch(function (e) { showError(e.message); });
    });

    refreshBtn.addEventListener("click", function () {
        load().catch(function (e) { showError(e.message); });
    });

    document.addEventListener("click", function (event) {
        var button = event.target.closest("[data-delete]");
        if (button) {
            var id = button.getAttribute("data-delete");
            fetch("/api/webhooks/" + id, {
                method: "DELETE",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to delete webhook");
                return load();
            }).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-suspend]");
        if (button) {
            var suspendId = button.getAttribute("data-suspend");
            fetch("/api/admin/users/" + suspendId + "/deactivate", {
                method: "POST",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to suspend account");
                return loadAdmin();
            }).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-restore]");
        if (button) {
            var restoreId = button.getAttribute("data-restore");
            fetch("/api/admin/users/" + restoreId + "/activate", {
                method: "POST",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to restore account");
                return loadAdmin();
            }).catch(function (e) { showError(e.message); });
        }
    });

    document.getElementById("admin-toggle").addEventListener("click", function () {
        var section = document.getElementById("admin-section");
        section.hidden = !section.hidden;
        if (!section.hidden) {
            loadAdmin().catch(function (e) { showError(e.message); });
        }
    });

    document.getElementById("export-csv").addEventListener("click", function () {
        exportCsv().catch(function (e) { showError(e.message); });
    });

    var form = document.getElementById("webhook-form");
    form.addEventListener("submit", function (event) {
        event.preventDefault();
        var urlInput = document.getElementById("webhook-url");
        var nameInput = document.getElementById("webhook-name");
        var channelSelect = document.getElementById("webhook-channel");
        var events = ["failureDetected", "fixPendingReview", "fixPushed", "needsHumanReview", "notCodeError"];
        var payload = {
            url: urlInput.value.trim(),
            events: events,
            channel: channelSelect.value
        };
        var name = nameInput.value.trim();
        if (name) payload.name = name;
        fetch("/api/webhooks", {
            method: "POST",
            headers: { "Content-Type": "application/json", Authorization: "Bearer " + token },
            body: JSON.stringify(payload)
        }).then(function (response) {
            if (!response.ok) throw new Error("Failed to add webhook");
            urlInput.value = "";
            nameInput.value = "";
            return load();
        }).catch(function (e) { showError(e.message); });
    });

    if (token) {
        tokenInput.value = token;
        showSession();
        load().catch(function (e) {
            sessionUser.textContent = "";
            tokenInput.hidden = false;
            connectBtn.hidden = false;
            showError(e.message);
        });
    }
})();