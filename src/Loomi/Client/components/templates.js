/** What a template asks of the person using it, in one line; nothing at all when it takes no photos. */
export function photoHint(min, max, t, locale) {
  const n = v => Number(v).toLocaleString(locale);
  if (!max) return '';
  if (min === max) return min === 1 ? t('photosOne') : t('photosExact').replace('{n}', n(min));
  if (!min) return t('photosOptional').replace('{max}', n(max));
  return t('photosRange').replace('{min}', n(min)).replace('{max}', n(max));
}
/** "1" or "1–2", in the reader's digits. */
export function photoSpan(min, max, locale) {
  const n = v => Number(v).toLocaleString(locale);
  return min === max ? n(min) : `${n(min)}–${n(max)}`;
}
/** A replaced sample keeps its URL, so the browser would go on showing the old one without the template's own timestamp on it. */
export function sampleSrc(template) {
  if (!template?.sampleUrl) return null;
  return `${template.sampleUrl}${template.sampleUrl.includes('?') ? '&' : '?'}v=${encodeURIComponent(template.updatedAt || '')}`;
}
