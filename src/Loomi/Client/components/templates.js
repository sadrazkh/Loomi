import { t, tf, n } from '../core';
/** What a template asks of the person using it, in one line; nothing at all when it takes no photos. */
export function photoHint(min, max) {
  if (!max) return '';
  if (min === max) return min === 1 ? t('stPhotosOne') : tf('stPhotosExact', min);
  if (!min) return tf('stPhotosOptional', max);
  return tf('stPhotosRange', min, max);
}
/** "1" or "1–2", in the reader's digits. */
export const photoSpan = (min, max) => min === max ? n(min) : `${n(min)}–${n(max)}`;
/** The short line under a template's card: how many photos it takes. */
export const photoMeta = (min, max) => !max ? t('stNoPhotos') : min === 1 && max === 1 ? t('stPhotosMetaOne') : tf('stPhotosMeta', photoSpan(min, max));
/** A replaced sample keeps its URL, so the browser would go on showing the old one without the template's own timestamp on it. */
export function sampleSrc(template) {
  if (!template?.sampleUrl) return null;
  return `${template.sampleUrl}${template.sampleUrl.includes('?') ? '&' : '?'}v=${encodeURIComponent(template.updatedAt || '')}`;
}
/** The public list is categories holding templates; most places want the templates alone, each knowing its category's name. */
export const flatten = categories => (categories || []).flatMap(c => (c.templates || []).map(x => ({ ...x, category: c.name })));
