import { ref } from 'vue';
import { t, tf, me, isOwner, payingPackage } from '../../core';
// Small things the image-making pages share.

// How the next request will be paid, said beside the button that sends it, so nobody learns the price only from their balance afterwards.

/** A package's own name, or what it gives when the owner left the name blank. */
export const packageName = p => p.name || (p.kind === 'Unlimited' ? t('shPkgUnlimited') : p.kind === 'Daily' ? tf('shPkgDaily', p.amount) : tf('shPkgCredits', p.amount));

/** The line in three parts, so the package's name can stand out: { lead, name, tail }, or null for the owner, who is never charged.
 *  price is a template's own price when it is known; without it the line names only what is left. */
export function payLine(price = null) {
  if (isOwner.value) return null;
  const p = payingPackage.value;
  if (p) {
    // A credit package is spent like credits; the other two make the image free while today's room lasts.
    const marked = p.kind === 'Credits' ? tf('stPayPackageCredits', '\u0000', p.remaining ?? p.amount) : tf('stPayFree', '\u0000');
    const [lead, tail = ''] = marked.split('\u0000');
    return { lead, name: packageName(p), tail };
  }
  if (me.value?.credits == null) return null;
  return { lead: price != null ? tf('stPayPrice', price, me.value.credits) : tf('stPayCredits', me.value.credits), name: '', tail: '' };
}

/** "1 message" or "3 messages": English needs the singular; Persian reads the same either way, so its two keys can hold one text. */
export const counted = (c, one, many) => c === 1 ? t(one) : tf(many, c);

/** True on a phone-width screen, the same width at which the shell trades its side menu for the bottom bar. */
const query = typeof matchMedia === 'function' ? matchMedia('(max-width: 860px)') : null;
export const narrow = ref(!!query?.matches);
query?.addEventListener?.('change', e => { narrow.value = e.matches; });
