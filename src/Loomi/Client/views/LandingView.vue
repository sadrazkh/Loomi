<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue';
import { Send, LogIn, ExternalLink } from 'lucide-vue-next';
import LoomiMark from '../components/LoomiMark.vue';
import PrefControls from '../components/PrefControls.vue';
import HeroCollage from '../components/landing/HeroCollage.vue';
import HeroArt from '../components/landing/HeroArt.vue';
import { sized, CARD } from '../images.js';
import { t, tf, n, theme, api } from '../core';
// The page someone arriving from an advert lands on. Everything specific comes from the server and is the owner's own: no samples means the drawn hero and no
// showcase, no prices means no prices section. Nothing here is a claim the owner did not make, and a failed request leaves a page that is still whole.
const EMPTY = { botUsername: null, signupOpen: false, giftImages: null, contact: null, prices: [], showcase: [] };
const site = ref(null);
onMounted(async () => { try { site.value = await api('/api/public/site'); } catch { site.value = EMPTY; } });

const telegram = computed(() => site.value?.signupOpen && site.value.botUsername ? `https://t.me/${encodeURIComponent(site.value.botUsername)}?start` : null);
const startLabel = computed(() => { const gift = site.value?.giftImages; return gift > 1 ? tf('laStartGift', gift) : gift === 1 ? t('laStartGiftOne') : t('laStart'); });

// A picture that fails to load is dropped everywhere it appears, so a stranger never sees a broken frame.
const broken = ref(new Set());
const drop = id => { broken.value = new Set(broken.value).add(id); };
const showcase = computed(() => (site.value?.showcase || []).filter(s => !broken.value.has(s.id)));
const categories = computed(() => [...new Set(showcase.value.map(s => s.category))]);
const category = ref(null);
const shown = computed(() => category.value ? showcase.value.filter(s => s.category === category.value) : showcase.value);

const prices = computed(() => site.value?.prices || []);
const contact = computed(() => site.value?.contact || '');
// The server only keeps a handle or an https address; this says so again because whatever is here becomes a link.
const contactLink = computed(() => /^@\w+$/.test(contact.value) ? `https://t.me/${contact.value.slice(1)}` : /^https:\/\//i.test(contact.value) ? contact.value : null);

const STEPS = [['laStep1', 'laStep1Body'], ['laStep2', 'laStep2Body'], ['laStep3', 'laStep3Body']];
const QUESTIONS = [['laQ1', 'laA1'], ['laQ2', 'laA2'], ['laQ3', 'laA3'], ['laQ4', 'laA4'], ['laQ5', 'laA5']];

// On a phone language and theme do not fit in the header, so its moon or sun opens them, as it does in the signed-in app.
const prefs = ref(false), prefsRoot = ref(null);
function closePrefs(e) { if (prefs.value && !prefsRoot.value?.contains(e.target)) prefs.value = false; }
onMounted(() => document.addEventListener('mousedown', closePrefs));
onBeforeUnmount(() => document.removeEventListener('mousedown', closePrefs));
</script>
<template>
 <div class="landing">
  <header class="lhead">
   <div class="wrap strip">
    <a class="brand" href="#/"><span class="mark"><LoomiMark :size="20"/></span>Loomi</a>
    <span class="grow"/>
    <PrefControls class="prefs-wide"/>
    <div ref="prefsRoot" class="prefs-narrow">
     <button class="btn btn-ghost btn-icon btn-sm emoji" type="button" :aria-label="t('shDisplay')" aria-haspopup="dialog" :aria-expanded="prefs" @click="prefs=!prefs">{{ theme==='dark' ? '🌙' : '☀️' }}</button>
     <div v-if="prefs" class="panel" role="dialog" :aria-label="t('shDisplay')"><PrefControls stacked/></div>
    </div>
    <a class="btn btn-sm" href="#/login"><LogIn/>{{ t('laSignIn') }}</a>
   </div>
  </header>

  <main>
   <section class="wrap hero">
    <div class="pitch">
     <h1>{{ t('laHero') }}</h1>
     <p class="lede"><template v-if="site">{{ telegram ? t('laLedeOpen') : t('laLede') }}</template></p>
     <span v-if="!site" class="skel go-wait"></span>
     <a v-else-if="telegram" class="btn btn-primary btn-lg go" :href="telegram" target="_blank" rel="noopener"><Send/>{{ startLabel }}</a>
     <a v-else class="btn btn-primary btn-lg go" href="#/login"><LogIn/>{{ t('laSignIn') }}</a>
    </div>
    <div class="visual">
     <template v-if="site">
      <HeroCollage v-if="showcase.length" :items="showcase" @broken="drop"/>
      <HeroArt v-else/>
     </template>
    </div>
   </section>

   <section v-if="showcase.length" class="wrap sec" aria-labelledby="la-make">
    <div class="sec-head"><h2 id="la-make">{{ t('laMakeTitle') }}</h2><p>{{ t('laMakeHelp') }}</p></div>
    <div v-if="categories.length > 1" class="chips" role="group" :aria-label="t('laFilter')">
     <button type="button" class="chip" :class="{ 'chip-on': !category }" :aria-pressed="!category" @click="category=null">{{ t('laAll') }}</button>
     <button v-for="c in categories" :key="c" type="button" class="chip" :class="{ 'chip-on': category===c }" :aria-pressed="category===c" dir="auto" @click="category=c">{{ c }}</button>
    </div>
    <ul class="works">
     <li v-for="s in shown" :key="s.id" class="work">
      <img :src="sized(s.sample, CARD)" alt="" width="640" height="800" loading="lazy" @error="drop(s.id)">
      <b dir="auto">{{ s.title }}</b>
      <small dir="auto">{{ s.category }}</small>
     </li>
    </ul>
   </section>

   <section class="wrap sec" aria-labelledby="la-how">
    <div class="sec-head"><h2 id="la-how">{{ t('laHowTitle') }}</h2></div>
    <ol class="steps">
     <li v-for="([title, body], i) in STEPS" :key="title"><span class="num" aria-hidden="true">{{ n(i + 1) }}</span><b>{{ t(title) }}</b><p>{{ t(body) }}</p></li>
    </ol>
    <a v-if="telegram" class="btn btn-primary btn-lg go" :href="telegram" target="_blank" rel="noopener"><Send/>{{ startLabel }}</a>
   </section>

   <section v-if="prices.length" class="wrap sec" aria-labelledby="la-prices">
    <div class="sec-head"><h2 id="la-prices">{{ t('laPricesTitle') }}</h2></div>
    <ul class="prices">
     <li v-for="(p, i) in prices" :key="i" class="price" :class="{ on: p.highlight }">
      <span v-if="p.highlight" class="pill">{{ t('laPick') }}</span>
      <b dir="auto">{{ p.name }}</b>
      <strong dir="auto">{{ p.price }}</strong>
      <p v-if="p.detail" dir="auto">{{ p.detail }}</p>
     </li>
    </ul>
    <p v-if="contact" class="buy">
     <span class="muted-text">{{ t('laBuy') }}</span>
     <bdi class="contact" dir="ltr">{{ contact }}</bdi>
     <a v-if="contactLink" class="btn btn-sm" :href="contactLink" target="_blank" rel="noopener"><ExternalLink/>{{ t('laOpenContact') }}</a>
    </p>
   </section>

   <section class="wrap sec" aria-labelledby="la-faq">
    <div class="sec-head"><h2 id="la-faq">{{ t('laFaqTitle') }}</h2></div>
    <dl class="faq">
     <div v-for="[q, a] in QUESTIONS" :key="q"><dt>{{ t(q) }}</dt><dd>{{ t(a) }}</dd></div>
    </dl>
   </section>
  </main>

  <footer class="lfoot">
   <div class="wrap strip">
    <span class="brand"><span class="mark"><LoomiMark :size="20"/></span>Loomi</span>
    <a v-if="contactLink" class="foot-contact" :href="contactLink" target="_blank" rel="noopener"><bdi dir="ltr">{{ contact }}</bdi></a>
    <span class="grow"/>
    <a class="btn btn-sm btn-ghost" href="#/login">{{ t('laSignIn') }}</a>
   </div>
  </footer>
 </div>
</template>
<style scoped>
.landing{min-height:100vh;overflow-x:clip;display:flex;flex-direction:column;font-size:15px;line-height:1.75;background:radial-gradient(60rem 30rem at 50% -8rem,var(--accent-soft),transparent 70%) no-repeat,var(--bg)}
main{flex:1}
/* The sides are named one by one: a notch has a physical side, whichever way the text runs. */
.wrap{width:100%;max-width:1120px;margin-inline:auto;padding-left:max(16px,env(safe-area-inset-left,0px));padding-right:max(16px,env(safe-area-inset-right,0px))}
.strip{display:flex;align-items:center;gap:8px;min-width:0}

.lhead{position:sticky;top:0;z-index:15;padding-top:env(safe-area-inset-top,0px);background:color-mix(in srgb,var(--bg) 86%,transparent);backdrop-filter:blur(10px);border-bottom:1px solid var(--line)}
.lhead .strip{height:56px}
.brand{display:flex;align-items:center;gap:8px;font-weight:700;font-size:17px;color:var(--text);text-decoration:none}
.mark{width:30px;height:30px;border-radius:var(--r2);background:var(--s3);display:grid;place-items:center}
.prefs-wide{display:none}
.prefs-narrow{position:relative}
.emoji{font-size:17px;font-family:'Segoe UI Emoji','Apple Color Emoji','Noto Color Emoji',sans-serif}
.panel{position:absolute;top:calc(100% + 8px);inset-inline-end:-90px;z-index:30;width:min(300px,calc(100vw - 32px));padding:14px;border-radius:var(--r);background:var(--s1);border:1px solid var(--line2);box-shadow:var(--shadow)}

.hero{display:flex;flex-direction:column;gap:30px;padding-top:32px;padding-bottom:12px}
.pitch{display:flex;flex-direction:column;gap:14px;min-width:0}
h1{font-size:clamp(27px,7.4vw,44px);line-height:1.3;font-weight:800;letter-spacing:-.005em}
.lede{color:var(--muted);font-size:15.5px;max-width:48ch;min-height:3.5em}
.go{height:auto;min-height:50px;padding:8px 22px;font-size:15.5px;white-space:normal;text-align:center}
.go svg.lucide{width:19px;height:19px}
.go-wait{display:block;height:50px;border-radius:var(--r2)}
.visual{width:100%;max-width:440px;margin-inline:auto;aspect-ratio:1/1}

.sec{display:flex;flex-direction:column;gap:18px;margin-top:56px}
.sec-head{display:flex;flex-direction:column;gap:6px}
.sec-head h2{font-size:22px;font-weight:800}
.sec-head p{color:var(--muted);max-width:60ch}
.sec>.go{margin-top:4px}

.chips{display:flex;flex-wrap:wrap;gap:8px}
.chips .chip{max-width:100%;overflow:hidden;text-overflow:ellipsis}
ul,ol,dl,dd{margin:0;padding:0;list-style:none}
.works{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:18px 12px}
.work{display:flex;flex-direction:column;min-width:0;line-height:1.5}
.work img{display:block;width:100%;height:auto;aspect-ratio:4/5;object-fit:cover;border-radius:var(--r);background:var(--s3);margin-bottom:8px}
.work b{font-size:14px;font-weight:600;overflow-wrap:anywhere}
.work small{font-size:12.5px;color:var(--muted);overflow-wrap:anywhere}

.steps{display:grid;grid-template-columns:minmax(0,1fr);gap:10px}
.steps li{display:grid;grid-template-columns:38px minmax(0,1fr);column-gap:12px;align-items:center;padding:14px;border:1px solid var(--line);border-radius:var(--r);background:var(--s1)}
.steps .num{grid-row:span 2;align-self:start;width:38px;height:38px;border-radius:50%;background:var(--accent-soft);color:var(--accent);display:grid;place-items:center;font-weight:800;font-size:17px;line-height:1}
.steps b{font-size:15px;font-weight:700}
.steps p{color:var(--muted);font-size:14px;line-height:1.7}

.prices{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(240px,100%),1fr));gap:12px}
.price{position:relative;display:flex;flex-direction:column;align-items:flex-start;gap:6px;min-width:0;padding:18px;border:1px solid var(--line);border-radius:var(--r);background:var(--s1)}
.price.on{border-color:var(--accent);box-shadow:0 0 0 1px var(--accent),var(--shadow-sm)}
.price b{font-size:15px;font-weight:600;max-width:100%;overflow-wrap:anywhere}
.price strong{font-size:22px;font-weight:800;line-height:1.35;color:var(--text);max-width:100%;overflow-wrap:anywhere}
.price.on strong{color:var(--accent)}
.price p{color:var(--muted);font-size:13.5px;line-height:1.7;max-width:100%;overflow-wrap:anywhere}
.buy{display:flex;flex-wrap:wrap;align-items:center;gap:8px 10px;min-width:0}
.contact{font-weight:600;user-select:all;overflow-wrap:anywhere;min-width:0}

.faq{display:grid;grid-template-columns:minmax(0,1fr);gap:20px}
.faq dt{font-weight:700;font-size:15px;margin-bottom:2px}
.faq dd{color:var(--muted);font-size:14.5px;line-height:1.85}

.lfoot{margin-top:64px;border-top:1px solid var(--line);padding-block:14px calc(14px + env(safe-area-inset-bottom,0px))}
.lfoot .strip{flex-wrap:wrap;gap:8px 14px}
.foot-contact{color:var(--muted);font-size:13.5px;text-decoration:none;overflow-wrap:anywhere;min-width:0}
.foot-contact:hover{color:var(--accent)}

@media (min-width:560px){
 .lede{min-height:1.75em}
 .works{grid-template-columns:repeat(3,minmax(0,1fr))}
}
@media (min-width:760px){
 .prefs-wide{display:flex}
 .prefs-narrow{display:none}
 .steps{grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}
 .steps li{display:flex;flex-direction:column;align-items:flex-start;gap:6px;padding:18px}
 .steps .num{margin-bottom:4px}
 .faq{grid-template-columns:repeat(2,minmax(0,1fr));gap:26px 40px}
 .go{align-self:flex-start;min-width:240px}
 .sec{margin-top:80px}
 .sec-head h2{font-size:26px}
}
@media (min-width:980px){
 .hero{display:grid;grid-template-columns:minmax(0,1.05fr) minmax(0,1fr);align-items:center;gap:48px;padding-top:56px;padding-bottom:24px}
 .pitch{gap:18px}
 .lede{font-size:17px}
 .visual{max-width:none}
 .works{grid-template-columns:repeat(4,minmax(0,1fr));gap:22px 16px}
}
</style>
