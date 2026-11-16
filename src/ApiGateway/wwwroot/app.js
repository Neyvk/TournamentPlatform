const API = "/api/v1";
const TOKEN_KEY = "tournament-access-token";
const state = { token: localStorage.getItem(TOKEN_KEY), userId: "", nickname: "", view: "profile", players: [], teams: [], tournaments: [], matches: [], ratingTeamId: "" };

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

function showMessage(message, error = false) {
  const box = $("#message");
  box.textContent = message;
  box.classList.toggle("error", error);
  box.hidden = false;
  window.clearTimeout(showMessage.timer);
  showMessage.timer = window.setTimeout(() => box.hidden = true, 5000);
}

function decodeToken(token) {
  try { return JSON.parse(decodeURIComponent(escape(atob(token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/"))))); }
  catch { return {}; }
}

async function api(path, options = {}) {
  const headers = { ...(options.body ? { "Content-Type": "application/json" } : {}), ...(state.token ? { Authorization: `Bearer ${state.token}` } : {}), ...options.headers };
  const response = await fetch(`${API}${path}`, { ...options, headers });
  const text = await response.text();
  let body = null;
  if (text) { try { body = JSON.parse(text); } catch { body = text; } }
  if (!response.ok) {
    const detail = body?.error || body?.title || body?.detail || Object.values(body?.errors || {}).flat().join(" ") || `Запрос завершился с кодом ${response.status}.`;
    throw new Error(detail);
  }
  return body;
}

function setSession(token) {
  state.token = token;
  localStorage.setItem(TOKEN_KEY, token);
  const claims = decodeToken(token);
  state.userId = claims.sub || "";
  state.nickname = claims.nickname || "";
  $("#navigation").hidden = false;
  $("#auth-view").hidden = true;
  $("#account").innerHTML = `<span>${escapeHtml(state.nickname || state.userId)}</span><button class="secondary" id="logout">Выйти</button>`;
  $("#logout").addEventListener("click", logout);
  openView(state.view);
}

function logout() {
  state.token = null; state.userId = ""; state.nickname = "";
  localStorage.removeItem(TOKEN_KEY);
  $("#navigation").hidden = true;
  $("#auth-view").hidden = false;
  $("#account").replaceChildren();
  $$("main > section").forEach(section => section.hidden = section.id !== "auth-view");
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[char]));
}

function playerName(userId) {
  return state.players.find(player => player.userId === userId)?.nickname || userId;
}
function teamName(teamId) {
  return state.teams.find(team => team.id === teamId)?.name || teamId;
}
function formatName(format) {
  return ({ olympic: "Олимпийская", round_robin: "Круговая", swiss: "Швейцарская" })[format] || format;
}
function statusName(status) {
  return ({ registration: "Регистрация", in_progress: "Идёт", completed: "Завершён", scheduled: "Ожидает", completed: "Завершён" })[status] || status;
}

function openView(name) {
  state.view = name;
  $$("main > section").forEach(section => section.hidden = section.id !== `${name}-view`);
  $$("nav button").forEach(button => button.classList.toggle("active", button.dataset.view === name));
  refreshView().catch(error => showMessage(error.message, true));
}

async function refreshBase() {
  [state.players, state.teams, state.tournaments] = await Promise.all([
    api("/players"), api("/teams"), api("/tournaments")
  ]);
}

async function refreshView() {
  if (state.view === "profile") {
    state.players = await api("/players");
    $("#players-list").innerHTML = state.players.map(player => `<article class="card"><strong>${escapeHtml(player.nickname)}</strong><div class="muted">${escapeHtml(player.userId)}</div></article>`).join("") || emptyMessage("Игроков пока нет.");
  }
  if (state.view === "teams") {
    await refreshBase();
    renderTeams();
  }
  if (state.view === "tournaments") {
    await refreshBase();
    renderTournaments();
  }
  if (state.view === "matches") {
    await refreshBase();
    await loadMatches();
  }
  if (state.view === "rating") {
    await refreshBase();
    await loadLeaderboard();
    if (state.ratingTeamId) await loadTeamRating(state.ratingTeamId);
  }
}

let autoRefreshRunning = false;
async function refreshAutomatically() {
  const active = document.activeElement;
  if (!state.token || document.hidden || autoRefreshRunning || ["INPUT", "SELECT", "TEXTAREA"].includes(active?.tagName)) return;
  autoRefreshRunning = true;
  try { await refreshView(); }
  catch { /* A later refresh will retry if an API is temporarily unavailable. */ }
  finally { autoRefreshRunning = false; }
}

window.setInterval(refreshAutomatically, 3000);
document.addEventListener("visibilitychange", () => {
  if (!document.hidden) refreshAutomatically();
});

function emptyMessage(message) { return `<p class="muted">${escapeHtml(message)}</p>`; }

function renderTeams() {
  const host = $("#teams-list");
  host.innerHTML = state.teams.map(team => {
    const owner = team.ownerId === state.userId;
    const member = team.members.includes(state.userId);
    const memberRows = team.members.map(id => `<div class="member"><span>${escapeHtml(playerName(id))}${id === team.ownerId ? " · владелец" : ""}</span>${owner && id !== team.ownerId ? `<button class="danger" data-action="kick" data-team="${team.id}" data-player="${escapeHtml(id)}">Исключить</button>` : ""}</div>`).join("");
    let actions = "";
    if (state.token && owner) actions = `<button class="danger" data-action="delete-team" data-team="${team.id}">Удалить команду</button>`;
    else if (state.token && member) actions = `<button class="secondary" data-action="leave-team" data-team="${team.id}">Покинуть команду</button>`;
    else if (state.token) actions = `<button data-action="join-team" data-team="${team.id}">Вступить</button>`;
    return `<article class="card"><div class="card-head"><h2>${escapeHtml(team.name)}</h2><span class="muted">${team.members.length} игроков</span></div><div class="muted">Владелец: ${escapeHtml(playerName(team.ownerId))}</div><div>${memberRows}</div><div class="actions">${actions}</div></article>`;
  }).join("") || emptyMessage("Команд пока нет.");
}

function renderTournaments() {
  const host = $("#tournaments-list");
  host.innerHTML = state.tournaments.map(tournament => {
    const owner = tournament.ownerId === state.userId;
    const ownedTeams = state.teams.filter(team => team.ownerId === state.userId);
    const register = state.token && tournament.status === "registration" && ownedTeams.length
      ? `<form data-action="register-team" data-tournament="${tournament.id}" class="inline-form"><label>Моя команда<select name="teamId">${ownedTeams.map(team => `<option value="${team.id}">${escapeHtml(team.name)}</option>`).join("")}</select></label><button>Зарегистрировать</button></form>` : "";
    const start = state.token && owner && tournament.status === "registration" ? `<button data-action="start-tournament" data-tournament="${tournament.id}">Запустить</button>` : "";
    return `<article class="card"><div class="card-head"><h2>${escapeHtml(tournament.name)}</h2><strong>${escapeHtml(statusName(tournament.status))}</strong></div><div class="muted">${escapeHtml(formatName(tournament.format))} · до ${tournament.maxTeams} команд${tournament.roundCount ? ` · ${tournament.roundCount} раундов` : ""}</div><div class="muted">Организатор: ${escapeHtml(playerName(tournament.ownerId))}</div>${register}<div class="actions">${start}<button class="secondary" data-action="show-matches" data-tournament="${tournament.id}">Матчи турнира</button></div></article>`;
  }).join("") || emptyMessage("Турниров пока нет.");
}

async function loadMatches(selectedTournamentId) {
  const selector = $("#match-tournament");
  if (!state.tournaments.length) {
    selector.innerHTML = ""; $("#matches-list").innerHTML = emptyMessage("Турниров пока нет."); return;
  }
  const selected = selectedTournamentId || selector.value || state.tournaments[0].id;
  selector.innerHTML = state.tournaments.map(t => `<option value="${t.id}">${escapeHtml(t.name)}</option>`).join("");
  selector.value = selected;
  state.matches = await api(`/matches/tournaments/${selected}`);
  const tournament = state.tournaments.find(item => item.id === selected);
  $("#matches-list").innerHTML = state.matches.map(match => {
    const canSetResult = state.token && tournament?.ownerId === state.userId && match.status !== "completed" && match.participantBId;
    const result = canSetResult ? `<form class="inline-form" data-action="set-result" data-match="${match.id}"><label>Счёт команды 1<input name="scoreA" type="number" min="0" value="0" required></label><label>Счёт команды 2<input name="scoreB" type="number" min="0" value="1" required></label><button>Сохранить результат</button></form>` : "";
    const score = match.scoreA == null ? "" : ` · ${match.scoreA}:${match.scoreB}`;
    return `<article class="card"><div class="card-head"><h2>Раунд ${match.round}</h2><span>${escapeHtml(statusName(match.status))}${score}</span></div><div>${escapeHtml(teamName(match.participantAId))} — ${escapeHtml(match.participantBId ? teamName(match.participantBId) : "техническая победа")}</div>${result}</article>`;
  }).join("") || emptyMessage("В этом турнире пока нет матчей.");
}

async function loadLeaderboard() {
  const ratings = await api("/ratings/leaderboard?limit=50");
  $("#leaderboard").innerHTML = ratings.map(entry => `<article class="card"><div class="card-head"><strong>#${entry.rank} ${escapeHtml(teamName(entry.teamId))}</strong><strong>${entry.value}</strong></div><div class="muted">Матчей: ${entry.matchesPlayed}</div></article>`).join("") || emptyMessage("Рейтинг пока пуст.");
  const select = $("#rating-form select[name=teamId]");
  const selectedTeamId = state.ratingTeamId || select.value;
  select.innerHTML = state.teams.map(team => `<option value="${team.id}">${escapeHtml(team.name)}</option>`).join("");
  if (state.teams.some(team => team.id === selectedTeamId)) select.value = selectedTeamId;
  if (!state.teams.length) $("#team-rating").innerHTML = emptyMessage("Команд пока нет.");
}

async function loadTeamRating(teamId) {
  try {
    const rating = await api(`/ratings/teams/${teamId}`);
    $("#team-rating").innerHTML = `<article class="card"><strong>${escapeHtml(teamName(teamId))}: ${rating.value}</strong><div class="muted">Матчей: ${rating.matchesPlayed}</div></article>`;
  } catch (error) { $("#team-rating").innerHTML = emptyMessage(error.message); }
}

$("#login-form").addEventListener("submit", async event => {
  event.preventDefault();
  const data = Object.fromEntries(new FormData(event.currentTarget));
  try { const result = await api("/auth/login", { method: "POST", body: JSON.stringify(data) }); setSession(result.accessToken); }
  catch (error) { showMessage(error.message, true); }
});

$("#register-form").addEventListener("submit", async event => {
  event.preventDefault();
  const data = Object.fromEntries(new FormData(event.currentTarget));
  try {
    await api("/auth/register", { method: "POST", body: JSON.stringify(data) });
    const login = await api("/auth/login", { method: "POST", body: JSON.stringify({ login: data.email, password: data.password }) });
    setSession(login.accessToken);
    showMessage("Аккаунт и профиль игрока созданы.");
  } catch (error) { showMessage(error.message, true); }
});

$("#team-form").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  try { await api("/teams", { method: "POST", body: JSON.stringify(Object.fromEntries(new FormData(form))) }); form.reset(); await refreshView(); showMessage("Команда создана."); }
  catch (error) { showMessage(error.message, true); }
});

$("#tournament-form [name=format]").addEventListener("change", event => {
  const limits = { olympic: 128, round_robin: 16, swiss: 256 };
  $("#tournament-form [name=maxTeams]").max = limits[event.target.value];
  $("#round-count-field").hidden = event.target.value !== "swiss";
});
$("#tournament-form").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const values = Object.fromEntries(new FormData(form));
  const body = { name: values.name, format: values.format, maxTeams: Number(values.maxTeams), roundCount: values.format === "swiss" ? Number(values.roundCount) : null };
  try { await api("/tournaments", { method: "POST", body: JSON.stringify(body) }); form.reset(); await refreshView(); showMessage("Турнир создан."); }
  catch (error) { showMessage(error.message, true); }
});

$("#match-tournament").addEventListener("change", event => loadMatches(event.target.value).catch(error => showMessage(error.message, true)));
$("#rating-form").addEventListener("submit", async event => {
  event.preventDefault();
  const id = new FormData(event.currentTarget).get("teamId");
  if (!id) return;
  state.ratingTeamId = id;
  await loadTeamRating(id);
});

document.addEventListener("click", async event => {
  const button = event.target.closest("button[data-action]");
  if (!button) return;
  const { action, team: teamId, player: playerId, tournament: tournamentId } = button.dataset;
  try {
    if (action === "join-team") await api(`/teams/${teamId}/members`, { method: "POST" });
    if (action === "leave-team") await api(`/teams/${teamId}/members/me`, { method: "DELETE" });
    if (action === "kick") await api(`/teams/${teamId}/members/${encodeURIComponent(playerId)}`, { method: "DELETE" });
    if (action === "delete-team") {
      if (!confirm("Удалить команду?")) return;
      await api(`/teams/${teamId}`, { method: "DELETE" });
    }
    if (action === "start-tournament") {
      if (!confirm("Запустить турнир? Регистрация команд закроется.")) return;
      await api(`/tournaments/${tournamentId}/start`, { method: "POST" });
      await refreshView(); showMessage("Турнир запущен."); return;
    }
    if (action === "show-matches") { openView("matches"); await loadMatches(tournamentId); return; }
    await refreshView();
  } catch (error) { showMessage(error.message, true); }
});

document.addEventListener("submit", async event => {
  const form = event.target.closest("form[data-action]");
  if (!form) return;
  event.preventDefault();
  const values = Object.fromEntries(new FormData(form));
  try {
    if (form.dataset.action === "register-team") {
      await api(`/tournaments/${form.dataset.tournament}/teams`, { method: "POST", body: JSON.stringify({ teamId: values.teamId }) });
      await refreshView(); showMessage("Команда зарегистрирована.");
    }
    if (form.dataset.action === "set-result") {
      await api(`/matches/${form.dataset.match}/result`, { method: "PUT", body: JSON.stringify({ scoreA: Number(values.scoreA), scoreB: Number(values.scoreB) }) });
      await loadMatches($("#match-tournament").value); showMessage("Результат сохранён.");
    }
  } catch (error) { showMessage(error.message, true); }
});

$("nav").addEventListener("click", event => {
  const button = event.target.closest("button[data-view]");
  if (button) openView(button.dataset.view);
});

if (state.token) setSession(state.token);
