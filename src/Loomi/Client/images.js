/** The widths the server keeps copies at, one per kind of place: a list's thumbnail, a card, the picture in a chat, the picture on its own. */
export const THUMB = 256, CARD = 640, VIEW = 1024, FULL = 1600;
/** A picture's address asking for a WebP copy no wider than w: the original is a PNG of a few megabytes, which on a slow connection kept even a
 *  thumbnail waiting. A download keeps the plain address, which is the original. */
export const sized = (url, w) => url ? `${url}${url.includes('?') ? '&' : '?'}w=${w}` : url;
/** A photo sent with a message is an upload or an earlier picture; only a picture has copies to ask for. */
export const inputSrc = input => input.generationId ? sized(input.url, THUMB) : input.url;
