// What My account and the management pages share: how a package reads, how a ledger line reads, a few formats core does not have,
// and the hand-over that lets the overview open one person's drawer on the users page.
import { ref } from 'vue';
import { Infinity as InfinityIcon, CalendarDays, Coins } from 'lucide-vue-next';
import { t, tf, lang, locale, me, when, daysLeft } from '../../core';

export const KINDS = ['Credits', 'Unlimited', 'Daily'];
export const KIND_ICON = { Unlimited: InfinityIcon, Daily: CalendarDays, Credits: Coins };
/** The bounds the server holds a package to, per kind; Unlimited's amount is its daily cap, where zero is none. */
export const AMOUNT_LIMITS = { Credits: [1, 1000000], Unlimited: [0, 10000], Daily: [1, 10000] };
export const MAX_DAYS = 3650;

/** Set before moving to the users page, which opens that person's drawer (on the given tab) as it arrives, then clears it. */
export const pendingUser = ref(null);

const faDigits = v => String(v).replace(/\d/g, d => '۰۱۲۳۴۵۶۷۸۹'[d]);
// The server names a package in Persian when the owner leaves the name empty; an English reader is shown the same name in English.
const serverName = p => p.kind === 'Credits' ? `${faDigits(p.amount)} اعتبار` : p.kind === 'Unlimited' ? (p.amount ? `نامحدود، روزی تا ${faDigits(p.amount)}` : 'نامحدود') : `روزی ${faDigits(p.amount)} تصویر`;
export const defaultName = (kind, amount) => kind === 'Credits' ? tf('pkNameCredits', amount) : kind === 'Unlimited' ? (amount ? tf('pkNameUnlimitedCap', amount) : t('pkNameUnlimited')) : tf('pkNameDaily', amount);
export const pkgName = p => !p.name || (lang.value !== 'fa' && p.name === serverName(p)) ? defaultName(p.kind, p.amount) : p.name;

/** The day and month alone, as the package rows write an end: "15 Aban". */
export const dayMonth = iso => when(iso).toLocaleDateString(locale.value, { day: 'numeric', month: 'long' });
export const dateTime = iso => new Intl.DateTimeFormat(locale.value, { dateStyle: 'medium', timeStyle: 'short' }).format(when(iso));
/** A small share keeps one decimal, so one failure in two hundred does not read as none at all. */
export const percent = (part, whole) => whole ? new Intl.NumberFormat(locale.value, { style: 'percent', maximumFractionDigits: part && part / whole < .1 ? 1 : 0 }).format(part / whole) : '';
export const initial = name => (name || '?').slice(0, 1);
/** A name or label set inside a sentence keeps its own direction without turning the sentence around: an English username in a Persian line, say. */
export const bidi = s => '⁨' + (s ?? '') + '⁩';
/** The session carries no id, so the signed-in owner is recognised by name; names are unique without regard to case. */
export const isYou = u => !!me.value && !!u && String(u.username).toLowerCase() === String(me.value.username).toLowerCase();
/** Zero means no daily limit, and a pre-upgrade owner may still carry the old unbounded sentinel; neither is a number worth showing. */
export const limited = u => u.dailyQuota > 0 && u.dailyQuota <= 100000;
/** A number of days, with English's singular for one. */
export const days = d => d === 1 ? t('pkDay') : tf('pkDays', d);
export const totalDays = p => Math.max(1, Math.round((when(p.endsAt) - when(p.startsAt)) / 86400000));
export const endingSoon = p => p.state === 'Active' && daysLeft(p) <= 3;

/** What is left of what the package gives, for its bar: credits left, today's images left, or for an uncapped unlimited one the time left. */
export function pkgLeft(p) {
  if (p.state !== 'Active') return 0;
  if (p.kind === 'Unlimited' && !p.amount) { const whole = when(p.endsAt) - when(p.startsAt); return whole > 0 ? Math.min(1, Math.max(0, (when(p.endsAt) - Date.now()) / whole)) : 0; }
  return p.amount ? Math.min(1, Math.max(0, (p.remaining ?? p.amount) / p.amount)) : 0;
}
/** The line under a package's name: what is left of it now, when it starts, or how it ended. */
export function pkgDetail(p) {
  if (p.state === 'Scheduled') return tf('pkStartsOn', dayMonth(p.startsAt));
  if (p.state === 'Ended') return t(p.endedEarly ? 'pkEndedEarly' : 'pkEnded');
  if (p.kind === 'Credits') return tf('pkCreditsLeft', p.remaining ?? p.amount, p.amount);
  if (p.kind === 'Unlimited' && !p.amount) return tf('pkNoCapToday', p.usedToday);
  return tf('shTodayOf', p.usedToday, p.amount);
}
const STATE = { Active: 0, Scheduled: 1, Ended: 2 }, SPEND = { Unlimited: 0, Daily: 1, Credits: 2 };
/** Running ones in the order a request spends them, then the ones waiting by start, then the ended ones newest first. */
export const bySpendOrder = (a, b) => (STATE[a.state] ?? 3) - (STATE[b.state] ?? 3)
  || (a.state === 'Active' ? (SPEND[a.kind] - SPEND[b.kind]) || (when(a.endsAt) - when(b.endsAt))
    : a.state === 'Scheduled' ? when(a.startsAt) - when(b.startsAt) : when(b.endsAt) - when(a.endsAt));

/** One ledger movement in words. The opening balance is written by the server in English, so it is recognised and translated. */
export function entryText(e) {
  const from = e.packageName ? t('adSep') + tf('lgFromPackage', bidi(e.packageName)) : '';
  const note = e.note && e.note !== 'opening balance' ? e.note : '';
  if (e.kind === 'Charge') return t('lgCharge') + from;
  if (e.kind === 'Refund') return t('lgRefund') + from;
  if (e.kind === 'Adjust') return note ? tf('lgAdjustNote', bidi(note)) : t('lgAdjust');
  if (e.note === 'opening balance') return t('lgOpening');
  return note ? tf('lgGrantNote', bidi(note)) : t('lgGrant');
}
/** Signed the reader's way, so a charge and a grant read apart at a glance; Persian keeps the sign on the left of the figure, as the locale writes it. */
export const signed = v => new Intl.NumberFormat(locale.value, { signDisplay: 'exceptZero' }).format(v ?? 0);
