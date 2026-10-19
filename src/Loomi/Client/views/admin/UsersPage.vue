<script setup>
import { ref, computed, onMounted, onBeforeUnmount, nextTick } from 'vue';
import { Search, UserPlus, Users } from 'lucide-vue-next';
import Modal from '../../components/Modal.vue';
import UserDrawer from './UserDrawer.vue';
import { t, tf, n, api, ago, packageLabel, onTick } from '../../core';
import { KIND_ICON, pendingUser, endingSoon, limited, initial } from './kit';
// Everyone who can sign in: their running packages, credits and today's use in one table, and a drawer with everything else about one person.
const users = ref(null), usage = ref(null), query = ref(''), selected = ref(null), tab = ref('summary');
const dialog = ref(false), form = ref({}), busy = ref(false), error = ref('');
async function load() {
 const [list, today] = await Promise.all([api('/api/users').catch(() => null), api('/api/usage?period=today').catch(() => null)]);
 if (list) users.value = list; else users.value ??= [];
 if (today) usage.value = today;
 if (selected.value) selected.value = users.value.find(u => u.id === selected.value.id) || selected.value;
}
const lastActive = computed(() => Object.fromEntries((usage.value?.users || []).map(u => [u.userId, u.lastActive])));
const shown = computed(() => {
 const q = query.value.trim().toLocaleLowerCase();
 return (users.value || []).filter(u => !q || u.username.toLocaleLowerCase().includes(q));
});
const running = computed(() => (users.value || []).reduce((sum, u) => sum + (u.packages || []).filter(p => p.state === 'Active').length, 0));
// Running ones first, then those waiting to start; the server sends them soonest ending first.
const pkgs = u => [...(u.packages || [])].sort((a, b) => (a.state === 'Active' ? 0 : 1) - (b.state === 'Active' ? 0 : 1));

let opener = null;
function open(u, on = 'summary') { opener = document.activeElement; tab.value = on; selected.value = u; }
async function closeDrawer() { selected.value = null; await nextTick(); opener?.focus?.(); }
async function removed() { selected.value = null; await load(); }

function newUser() { form.value = { username: '', password: '', dailyQuota: 0, credits: 1500 }; error.value = ''; dialog.value = true; }
// A new person usually needs a package next, so their drawer opens on that tab once they exist.
async function create() {
 if (busy.value) return; busy.value = true; error.value = '';
 try {
  const f = form.value;
  const made = await api('/api/users', 'POST', { username: f.username.trim(), password: f.password, dailyQuota: Number(f.dailyQuota) || 0, credits: Number(f.credits) || 0 });
  dialog.value = false; await load();
  open(users.value.find(u => u.id === made.id) || made, 'packages');
 } catch (e) { error.value = e.message; } finally { busy.value = false; }
}

onMounted(async () => {
 await load();
 // Sent here from the overview: open that person straight away.
 const wanted = pendingUser.value; pendingUser.value = null;
 if (wanted) { const u = users.value.find(x => x.id === wanted.id); if (u) open(u, wanted.tab || 'summary'); }
});
let beat = 0;
const stop = onTick(() => { if (++beat % 3 === 0) load(); });
onBeforeUnmount(stop);
</script>

<template>
 <div class="view users">
  <header class="view-head">
   <div><h1>{{ t('shUsers') }}</h1><p v-if="users">{{ tf('adUsersSub', users.length, running) }}</p></div>
   <div class="tools">
    <label class="search"><Search/><input v-model="query" type="search" :placeholder="t('adSearchUsers')" :aria-label="t('adSearchUsers')"></label>
    <button class="btn btn-primary" @click="newUser"><UserPlus/>{{ t('adNewUser') }}</button>
   </div>
  </header>

  <div v-if="!users" class="stack"><div v-for="i in 4" :key="i" class="skel" style="height:54px"></div></div>
  <div v-else-if="!users.length" class="blank"><Users/><h2>{{ t('noUsers') }}</h2><p>{{ t('usersHelp') }}</p></div>
  <p v-else-if="!shown.length" class="faint-text">{{ t('adNoUserMatch') }}</p>
  <div v-else class="card table-card">
   <table class="tbl">
    <thead><tr><th scope="col">{{ t('usageUser') }}</th><th scope="col">{{ t('adActivePackage') }}</th><th scope="col">{{ t('shCredits') }}</th><th scope="col">{{ t('shToday') }}</th><th scope="col">{{ t('usageLastActive') }}</th></tr></thead>
    <tbody>
     <tr v-for="u in shown" :key="u.id" :class="{ off: u.isDisabled }" :aria-selected="selected?.id === u.id" @click="open(u)">
      <td class="c-who">
       <button class="who" :aria-label="tf('adOpenUser', u.username)" @click.stop="open(u)">
        <span class="av">{{ initial(u.username) }}</span><b class="truncate"><bdi>{{ u.username }}</bdi></b>
        <span v-if="u.role === 'Owner'" class="tag">{{ t('shOwner') }}</span>
        <span v-if="u.isDisabled" class="pill pill-muted">{{ t('usageDisabled') }}</span>
       </button>
      </td>
      <td class="c-pkg">
       <span v-if="u.role === 'Owner'" class="faint-text">{{ t('adNoLimits') }}</span>
       <span v-else-if="!(u.packages || []).length" class="pill pill-muted">{{ t('adNoPackage') }}</span>
       <span v-else class="pills"><span v-for="p in pkgs(u)" :key="p.id" class="pill" :class="{ 'pill-warn': endingSoon(p), 'pill-muted': p.state === 'Scheduled' }"><component :is="KIND_ICON[p.kind]"/>{{ packageLabel(p) }}</span></span>
      </td>
      <td class="c-cred num" :data-label="t('shCredits')">{{ u.role === 'Owner' ? '—' : n(u.credits) }}</td>
      <td class="c-today num" :data-label="t('shToday')">{{ limited(u) ? tf('adOf', u.usedToday, u.dailyQuota) : n(u.usedToday) }}</td>
      <td class="c-last"><span class="muted-text">{{ lastActive[u.id] ? ago(lastActive[u.id]) : t(usage ? 'adNotYet' : 'shLoading') }}</span></td>
     </tr>
    </tbody>
   </table>
  </div>

  <UserDrawer v-if="selected" :user="selected" :tab="tab" @close="closeDrawer" @changed="load" @deleted="removed"/>

  <Modal v-if="dialog" :title="t('adNewUser')" :close-label="t('close')" @close="dialog=false">
   <form class="stack" @submit.prevent="create">
    <p class="muted-text">{{ t('userHelp') }}</p>
    <div class="field"><label for="nu-name">{{ t('username') }}</label><input id="nu-name" v-model="form.username" class="input" dir="ltr" autocomplete="off" required minlength="3" maxlength="64"><span class="field-help">{{ t('InvalidUsername') }}</span></div>
    <div class="field"><label for="nu-pass">{{ t('password') }}</label><input id="nu-pass" v-model="form.password" class="input" dir="ltr" type="password" autocomplete="new-password" required minlength="8" maxlength="512"><span class="field-help">{{ t('acPasswordRule') }}</span></div>
    <div class="two">
     <div class="field"><label for="nu-quota">{{ t('dailyQuota') }}</label><input id="nu-quota" v-model="form.dailyQuota" class="input num" type="number" min="0" max="10000" required><span class="field-help">{{ t('adQuotaHelp') }}</span></div>
     <div class="field"><label for="nu-credits">{{ t('adStartCredits') }}</label><input id="nu-credits" v-model="form.credits" class="input num" type="number" min="0" max="1000000" required></div>
    </div>
    <p v-if="error" role="alert" class="field-error">{{ t(error) }}</p>
    <div class="dlg-actions"><button type="button" class="btn" @click="dialog=false">{{ t('cancel') }}</button><button class="btn btn-primary" :disabled="busy || form.username.trim().length < 3 || form.password.length < 8">{{ busy ? t('processing') : t('adCreateUser') }}</button></div>
   </form>
  </Modal>
 </div>
</template>

<style scoped>
.view-head>.tools{flex-direction:row;flex-wrap:wrap;gap:10px;align-items:center}
.search{display:flex;align-items:center;gap:8px;height:38px;width:240px;padding:0 12px;border-radius:var(--r2);border:1px solid var(--line);background:var(--s1);color:var(--faint)}
.search:focus-within{border-color:var(--accent-line);box-shadow:0 0 0 3px var(--accent-soft)}
.search svg{width:16px;height:16px}
.search input{flex:1;min-width:0;border:0;background:none;outline:none;font-size:13.5px;color:var(--text)}
.table-card{padding:8px 6px 4px}
.tbl tbody tr{cursor:pointer}
.tbl td{height:56px;padding-block:8px}
.off td{color:var(--faint)}
.who{display:flex;align-items:center;gap:10px;border:0;background:none;padding:0;color:inherit;text-align:start;font:inherit;min-width:0;max-width:100%}
.who b{font-weight:600}
.pills{display:flex;flex-wrap:wrap;gap:6px}
.two{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}
@media (max-width:860px){
 .view-head>.tools{width:100%}
 .search{flex:1;width:auto}
 .table-card{padding:0;background:none;border:0}
 .tbl thead{display:none}
 .tbl,.tbl tbody{display:block}
 .tbl tbody{display:flex;flex-direction:column;gap:10px}
 .tbl tr{display:grid;grid-template-columns:minmax(0,1fr) auto;grid-template-areas:"who last" "pkg pkg" "cred today";gap:8px 12px;padding:14px;border:1px solid var(--line);border-radius:var(--r);background:var(--s1)}
 .tbl tr[aria-selected=true]{border-color:var(--accent-line)}
 .tbl td{display:block;height:auto;padding:0;border:0;background:none!important}
 .c-who{grid-area:who;min-width:0}.c-last{grid-area:last;font-size:12.5px;align-self:center}.c-pkg{grid-area:pkg}
 .c-cred{grid-area:cred}.c-today{grid-area:today;text-align:end}
 .c-cred::before,.c-today::before{content:attr(data-label) " ";color:var(--muted);font-size:12.5px}
}
@media (max-width:520px){.two{grid-template-columns:1fr}}
</style>
