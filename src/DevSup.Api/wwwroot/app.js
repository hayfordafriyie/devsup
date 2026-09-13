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

    async function load() {
        var overview = await api("/api/overview");
        var tickets = await api("/api/tickets");
        var webhooks = await api("/api/webhooks");
        renderCards(overview);
        renderRepositories(overview);
        renderTickets(tickets);
        renderWebhooks(webhooks);
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
        if (!button) return;
        var id = button.getAttribute("data-delete");
        fetch("/api/webhooks/" + id, {
            method: "DELETE",
            headers: { Authorization: "Bearer " + token }
        }).then(function (response) {
            if (!response.ok) throw new Error("Failed to delete webhook");
            return load();
        }).catch(function (e) { showError(e.message); });
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