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
            var status = row.paused
                ? { label: "Paused", kind: "muted" }
                : healthStatus(row);
            var checked = row.paused
                ? "since " + new Date(row.pausedAt).toLocaleString()
                : (row.appHealthCheckedAt ? new Date(row.appHealthCheckedAt).toLocaleString() : "never");
            var action = row.paused
                ? '<button data-repo-resume="' + row.id + '" title="Resume monitoring">Resume</button>'
                : '<button data-repo-pause="' + row.id + '" title="Pause monitoring">Pause</button>';
            action += '<button data-repo-members="' + row.id + '" title="Manage team access">Members</button>';
            return "<tr>" +
                "<td>" + escapeHtml(repoName(row.cloneUrl)) + "</td>" +
                "<td><a href=\"" + escapeHtml(row.appUrl || "#") + "\" target=\"_blank\" rel=\"noopener\">" + escapeHtml(row.appUrl || "not configured") + "</a></td>" +
                "<td>" + badge(status) + "</td>" +
                "<td>" + escaped(checked) + "</td>" +
                "<td>" + escaped(row.appHealthLastError) + "</td>" +
                "<td>" + action + "</td>" +
                "</tr>";
        }).join("");
        section.hidden = false;
    }

    var membersRepoId = null;

    function loadRepositoryMembers(repoId) {
        return fetch("/api/repositories/" + repoId + "/members", {
            headers: { Authorization: "Bearer " + token }
        }).then(function (response) {
            if (!response.ok) throw new Error("You do not have access to this repository");
            return response.json();
        }).then(function (data) {
            membersRepoId = repoId;
            var section = document.getElementById("repo-members-panel");
            var list = document.getElementById("repo-members-list");
            var form = document.getElementById("repo-member-form");
            var emailInput = document.getElementById("repo-member-email");
            if (data.members.length === 0) {
                list.innerHTML = '<li><span class="muted">No members yet &mdash; invite a teammate to collaborate.</span></li>';
            } else {
                list.innerHTML = data.members.map(function (m) {
                    var role = m.role === "Observer" ? "observer" : "operator";
                    var remove = data.owner
                        ? '<button data-member-remove="' + m.userId + '" data-member-repo="' + repoId + '" title="Revoke access">&times;</button>'
                        : "";
                    return "<li>" + badge({ label: role, kind: "muted" }) +
                        " " + escapeHtml(m.displayName) + " &lt;" + escapeHtml(m.email) + "&gt;" + remove + "</li>";
                }).join("");
            }
            form.hidden = !data.owner;
            emailInput.value = "";
            form.setAttribute("data-repo-id", repoId);
            section.hidden = false;
            return data;
        });
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
                '<td><button data-ticket-detail="' + t.id + '" title="Incident detail">View</button></td>' +
                "</tr>";
        }).join("");
        renderTickets.currentCount = tickets.length;
        section.hidden = false;
    }

    var currentTicketId = null;

    function renderTicketDetail(detail) {
        document.getElementById("ticket-detail-section").hidden = false;
        document.getElementById("ticket-detail-title").textContent = "#" + detail.id.slice(0, 8);

        var detailEl = document.getElementById("ticket-detail-body");
        var closeButton = detail.status && detail.status === "closed" ? "" :
            '<button data-ticket-close="' + detail.id + '">Close ticket</button>';
        var reopenButton = detail.status === "closed" ?
            '<button data-ticket-reopen="' + detail.id + '">Reopen ticket</button>' : "";
        var exception = detail.exceptionMessage
            ? '<pre class="detail-pre">' + escapeHtml(detail.exceptionMessage) + "</pre>"
            : '<p class="muted">No exception message recorded.</p>';
        var patch = detail.patchSummary
            ? "<p><strong>Patch:</strong> " + escapeHtml(detail.patchSummary) + "</p>" : "";
        var analysis = detail.analysis
            ? "<p><strong>Analysis:</strong> " + escapeHtml(detail.analysis) + "</p>" : "";
        var prLink = detail.pullRequestUrl
            ? ' <a href="' + escapeHtml(detail.pullRequestUrl) + '" target="_blank" rel="noopener">PR</a>' : "";
        var commit = detail.commitSha
            ? "<p><strong>Commit:</strong> <code>" + escapeHtml(detail.commitSha) + "</code></p>" : "";
        var lastError = detail.lastError
            ? "<p class=\"muted\"><strong>Last agent error:</strong> " + escapeHtml(detail.lastError) + "</p>" : "";

        detailEl.innerHTML =
            "<p><strong>" + escapeHtml(detail.method) + " " + escapeHtml(detail.path) + "</strong> " +
            "&middot; HTTP " + detail.statusCode + "</p>" +
            "<p>" + badge({ label: prettyStatus(detail.status), kind: kindFor(detail.status) }) + "</p>" +
            "<p class=\"muted\">Occurred " + new Date(detail.occurredAt).toLocaleString() + " &middot; " +
            "Updated " + new Date(detail.updatedAt).toLocaleString() + "</p>" +
            "<p><strong>Category:</strong> " + escapeHtml(detail.category + " \u00b7 " + detail.kind) + "</p>" +
            exception +
            patch +
            analysis +
            commit +
            prLink +
            lastError +
            "<p>" + closeButton + reopenButton + ' <button data-ticket-back="1">Back</button></p>';
    }

    function prettyStatus(status) {
        return String(status).replace(/([A-Z])/g, " $1").toLowerCase();
    }

    function kindFor(status) {
        switch (status) {
            case "closed":
            case "fixPushed":
            case "fixVerified": return "ok";
            case "needsHumanReview": return "bad";
            case "new":
            case "triaged":
            case "investigating":
            case "patchProposed":
            case "fixPendingReview": return "warn";
            default: return "muted";
        }
    }

    async function loadTicketDetail(ticketId) {
        currentTicketId = ticketId;
        var detail = await api("/api/tickets/" + ticketId);
        renderTicketDetail(detail);
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
                return "<li>" + channelBadge(w.channel) + " " + (w.active ? "" : '<span class="status muted">Inactive</span> ') + name +
                    ' <a href="' + escapeHtml(w.url) + '" target="_blank" rel="noopener">' + escapeHtml(w.url) + "</a>" +
                    '<span class="muted">&nbsp;&middot; ' + events + "</span>" +
                    (w.active
                        ? '<button data-webhook-pause="' + w.id + '" title="Stop delivering to this endpoint">Pause</button>'
                        : '<button data-webhook-resume="' + w.id + '" title="Resume delivering to this endpoint">Resume</button>') +
                    '<button data-log="' + w.id + '" title="Delivery log">Log</button>' +
                    '<button data-ping="' + w.id + '" title="Send a signed test ping">Ping</button>' +
                    '<button data-delete="' + w.id + '">Delete</button>' +
                    '<div class="delivery-log" data-log-target="' + w.id + '" hidden></div></li>';
            }).join("");
        }
        section.hidden = false;
    }

    function renderDeliveryLog(webhookId, page) {
        var container = document.querySelector('[data-log-target="' + webhookId + '"]');
        if (!container) {
            return;
        }
        if (page.items.length === 0) {
            container.innerHTML = '<p class="muted">No deliveries yet.</p>';
            container.hidden = false;
            return;
        }
        container.innerHTML = '<table class="delivery-log-table"><thead><tr>' +
            "<th>Event</th><th>Status</th><th>Attempts</th><th>Created</th><th>Last error</th><th></th>" +
            "</tr></thead><tbody>" + page.items.map(function (d) {
                var status = d.sent
                    ? '<span class="status ok">Sent</span>'
                    : '<span class="status warn">Pending</span>';
                var action = d.sent ? "" : '<button data-retry-delivery="' + d.id + '" data-webhook="' + webhookId + '">Retry</button>';
                return "<tr>" +
                    "<td>" + escapeHtml(d.event) + "</td>" +
                    "<td>" + status + "</td>" +
                    "<td>" + d.attempts + "</td>" +
                    "<td>" + escaped(new Date(d.createdAt).toLocaleString()) + "</td>" +
                    "<td>" + escaped(d.lastError) + "</td>" +
                    "<td>" + action + "</td>" +
                    "</tr>";
            }).join("") + "</tbody></table>";
        container.hidden = false;
    }

    async function toggleDeliveryLog(webhookId) {
        var container = document.querySelector('[data-log-target="' + webhookId + '"]');
        if (!container) {
            return;
        }
        if (!container.hidden) {
            container.hidden = true;
            container.innerHTML = "";
            return;
        }
        var page = await api("/api/webhooks/" + webhookId + "/deliveries?pageSize=20");
        renderDeliveryLog(webhookId, page);
    }

    var currentAccount = null;

    function renderAccount(account) {
        document.getElementById("account-section").hidden = false;
        var body = document.getElementById("account-body");
        body.innerHTML =
            "<p><strong>Name:</strong> " + escapeHtml(account.displayName) +
            "<br><strong>Email:</strong> <code>" + escapeHtml(account.email) + "</code></p>" +
            '<label class="pref-controls"><input type="checkbox" id="digest-toggle"' +
            (account.digestEnabled ? " checked" : "") +
            ' /> Send me the daily digest summary</label>' +
            '<p class="muted">The daily digest summarizes failures, open repair tickets and recently pushed fixes. Turning it off keeps transactional incident emails and webhook deliveries intact.</p>';
        body.querySelector("#digest-toggle").addEventListener("change", function () {
            if (!currentAccount) return;
            var enabled = this.checked;
            fetch("/api/account", {
                method: "PUT",
                headers: { "Content-Type": "application/json", Authorization: "Bearer " + token },
                body: JSON.stringify({ displayName: currentAccount.displayName, digestEnabled: enabled })
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to update digest preference");
                return loadAccount();
            }).catch(function (e) { showError(e.message); });
        });
    }

    async function loadAccount() {
        currentAccount = await api("/api/account");
        renderAccount(currentAccount);
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

    var PREF_EVENTS = [
        { event: "failureDetected", label: "Error detected" },
        { event: "notCodeError", label: "Not a code error" },
        { event: "fixPushed", label: "Fix pushed" },
        { event: "fixPendingReview", label: "Fix ready for review" },
        { event: "needsHumanReview", label: "Needs human review" }
    ];

    function renderPreferences(repositories, preferences) {
        var container = document.getElementById("preferences");
        if (repositories.length === 0) {
            container.innerHTML = '<p class="muted">No connected repositories.</p>';
            return;
        }
        container.innerHTML = repositories.map(function (repo) {
            var pref = (preferences || []).find(function (p) { return p.repositoryId === repo.id; });
            var emailEnabled = pref ? pref.emailEnabled : true;
            var muted = pref ? pref.mutedEvents : [];
            var master = '<label><input type="checkbox" data-repo="' + repo.id + '" data-field="emailEnabled"' +
                (emailEnabled ? " checked" : "") + '> Email notifications</label>';
            var events = PREF_EVENTS.map(function (e) {
                var checked = muted.indexOf(e.event) !== -1 ? " checked" : "";
                return '<label><input type="checkbox" data-repo="' + repo.id + '" data-event="' + e.event + '"' +
                    checked + "> " + e.label + "</label>";
            }).join("");
            return '<div class="pref-repo">' +
                '<div class="pref-title"><strong>' + escapeHtml(repoName(repo.cloneUrl)) + "</strong> <span class=\"muted\">" + escapeHtml(repo.cloneUrl) + "</span></div>" +
                '<div class="pref-controls">' + master + events + "</div>" +
                "</div>";
        }).join("");
    }

    async function loadPreferences() {
        var repos = await api("/api/repositories");
        document.getElementById("preferences-toggle").hidden = repos.length === 0;
        if (document.getElementById("preferences-section").hidden) return;
        var preferences = await api("/api/notification-preferences");
        renderPreferences(repos, preferences);
    }

    function savePreference(container) {
        var master = container.querySelector('[data-field="emailEnabled"]');
        var repoId = master.getAttribute("data-repo");
        var mutedEvents = Array.prototype.map.call(
            container.querySelectorAll('[data-event]:checked'),
            function (box) { return box.getAttribute("data-event"); }
        );
        return fetch("/api/notification-preferences", {
            method: "PUT",
            headers: { "Content-Type": "application/json", Authorization: "Bearer " + token },
            body: JSON.stringify({ repositoryId: repoId, emailEnabled: master.checked, mutedEvents: mutedEvents })
        }).then(function (response) {
            if (!response.ok) throw new Error("Failed to save notification preferences");
        });
    }

    function renderEmails(page) {
        var section = document.getElementById("delivery-section");
        var tbody = document.getElementById("emails").querySelector("tbody");
        if (!page || page.items.length === 0) {
            tbody.innerHTML = '<tr><td colspan="6" class="muted">No emails in your outbox.</td></tr>';
        } else {
            tbody.innerHTML = page.items.map(function (e) {
                var status = e.sent
                    ? '<span class="status ok">Sent</span>'
                    : '<span class="status warn">Queued</span>';
                var action = e.sent ? "" : '<button data-retry-email="' + e.id + '">Retry</button>';
                return "<tr>" +
                    "<td>" + escapeHtml(e.to) + "</td>" +
                    "<td>" + escapeHtml(e.subject) + "</td>" +
                    "<td>" + status + "</td>" +
                    "<td>" + e.attempts + "</td>" +
                    "<td>" + escaped(e.lastError) + "</td>" +
                    "<td>" + action + "</td>" +
                    "</tr>";
            }).join("");
        }
        section.hidden = false;
    }

    async function loadEmails() {
        var page = await api("/api/emails?pageSize=50");
        renderEmails(page);
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
        await loadPreferences();
        await loadEmails();
        await loadAccount();
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
        var button = event.target.closest("[data-repo-pause]");
        if (button) {
            var pauseId = button.getAttribute("data-repo-pause");
            fetch("/api/repositories/" + pauseId + "/pause", {
                method: "POST",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to pause repository");
                return load();
            }).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-repo-resume]");
        if (button) {
            var resumeId = button.getAttribute("data-repo-resume");
            fetch("/api/repositories/" + resumeId + "/unpause", {
                method: "POST",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to resume repository");
                return load();
            }).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-repo-members]");
        if (button) {
            loadRepositoryMembers(button.getAttribute("data-repo-members")).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-members-close]");
        if (button) {
            document.getElementById("repo-members-panel").hidden = true;
            return;
        }
        button = event.target.closest("[data-member-remove]");
        if (button) {
            var repoMemberRepo = button.getAttribute("data-member-repo");
            var memberUserId = button.getAttribute("data-member-remove");
            fetch("/api/repositories/" + repoMemberRepo + "/members/" + memberUserId, {
                method: "DELETE",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to remove member");
                return loadRepositoryMembers(repoMemberRepo);
            }).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-ticket-detail]");
        if (button) {
            loadTicketDetail(button.getAttribute("data-ticket-detail")).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-ticket-close]");
        if (button) {
            var closeId = button.getAttribute("data-ticket-close");
            fetch("/api/tickets/" + closeId + "/close", {
                method: "POST",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to close ticket");
                return loadTicketDetail(closeId);
            }).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-ticket-reopen]");
        if (button) {
            var reopenId = button.getAttribute("data-ticket-reopen");
            fetch("/api/tickets/" + reopenId + "/reopen", {
                method: "POST",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to reopen ticket");
                return loadTicketDetail(reopenId);
            }).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-ticket-back]");
        if (button) {
            document.getElementById("ticket-detail-section").hidden = true;
            return;
        }
        button = event.target.closest("[data-webhook-pause]");
        if (button) {
            var pauseHookId = button.getAttribute("data-webhook-pause");
            fetch("/api/webhooks/" + pauseHookId + "/deactivate", {
                method: "POST",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to pause webhook");
                return load();
            }).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-webhook-resume]");
        if (button) {
            var resumeHookId = button.getAttribute("data-webhook-resume");
            fetch("/api/webhooks/" + resumeHookId + "/activate", {
                method: "POST",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to resume webhook");
                return load();
            }).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-delete]");
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
        button = event.target.closest("[data-log]");
        if (button) {
            var logId = button.getAttribute("data-log");
            toggleDeliveryLog(logId).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-retry-delivery]");
        if (button) {
            var deliveryId = button.getAttribute("data-retry-delivery");
            var webhookId = button.getAttribute("data-webhook");
            fetch("/api/webhooks/" + webhookId + "/deliveries/" + deliveryId + "/retry", {
                method: "POST",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to retry delivery");
                return toggleDeliveryLog(webhookId);
            }).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-ping]");
        if (button) {
            var pingId = button.getAttribute("data-ping");
            fetch("/api/webhooks/" + pingId + "/test", {
                method: "POST",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to ping webhook");
                return loadEmails();
            }).catch(function (e) { showError(e.message); });
            return;
        }
        button = event.target.closest("[data-retry-email]");
        if (button) {
            var emailId = button.getAttribute("data-retry-email");
            fetch("/api/emails/" + emailId + "/retry", {
                method: "POST",
                headers: { Authorization: "Bearer " + token }
            }).then(function (response) {
                if (!response.ok) throw new Error("Failed to retry email");
                return loadEmails();
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

    document.getElementById("preferences-toggle").addEventListener("click", function () {
        var section = document.getElementById("preferences-section");
        section.hidden = !section.hidden;
        if (!section.hidden) {
            loadPreferences().catch(function (e) { showError(e.message); });
        }
    });

    document.getElementById("preferences").addEventListener("change", function (event) {
        var container = event.target.closest(".pref-repo");
        if (!container) return;
        savePreference(container).catch(function (e) { showError(e.message); });
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

    var memberForm = document.getElementById("repo-member-form");
    memberForm.addEventListener("submit", function (event) {
        event.preventDefault();
        var repoId = memberForm.getAttribute("data-repo-id");
        if (!repoId) return;
        var emailInput = document.getElementById("repo-member-email");
        var roleSelect = document.getElementById("repo-member-role");
        fetch("/api/repositories/" + repoId + "/members", {
            method: "POST",
            headers: { "Content-Type": "application/json", Authorization: "Bearer " + token },
            body: JSON.stringify({ email: emailInput.value.trim(), role: roleSelect.value })
        }).then(function (response) {
            if (!response.ok) throw new Error("Failed to invite member");
            emailInput.value = "";
            return loadRepositoryMembers(repoId);
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