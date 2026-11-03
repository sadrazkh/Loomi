<script setup>
// The hero with no samples to show: three drawn pictures in the shape the collage takes, so a workspace with no templates still has a whole page. A portrait,
// a woven star tile and a saffron dusk; the drawings keep their own colours in both themes, as a real picture would, and only their frames follow the theme.
</script>
<template>
 <svg class="art" viewBox="0 0 480 480" fill="none" aria-hidden="true">
  <defs>
   <radialGradient id="la-glow"><stop offset="0" stop-color="var(--accent)" stop-opacity=".2"/><stop offset="1" stop-color="var(--accent)" stop-opacity="0"/></radialGradient>
   <linearGradient id="la-sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#151a2b"/><stop offset=".55" stop-color="#3a2740"/><stop offset=".82" stop-color="#a7623a"/></linearGradient>
   <linearGradient id="la-sun" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#f7cf80"/><stop offset="1" stop-color="#e8a838"/></linearGradient>
   <linearGradient id="la-face" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#2c2f3d"/><stop offset="1" stop-color="#4a3323"/></linearGradient>
   <pattern id="la-star" width="44" height="44" patternUnits="userSpaceOnUse">
    <rect width="44" height="44" fill="#201a14"/>
    <g stroke="#e8a838" stroke-width="1.4"><rect x="13" y="13" width="18" height="18"/><rect x="13" y="13" width="18" height="18" transform="rotate(45 22 22)"/></g>
    <circle cx="22" cy="22" r="2.2" fill="#e8a838"/>
   </pattern>
   <clipPath id="la-a"><rect x="12" y="24" width="166" height="208" rx="18"/></clipPath>
   <clipPath id="la-b"><rect x="302" y="208" width="166" height="208" rx="18"/></clipPath>
   <clipPath id="la-c"><rect x="118" y="78" width="244" height="305" rx="22"/></clipPath>
  </defs>
  <circle cx="240" cy="240" r="226" fill="url(#la-glow)"/>

  <g transform="rotate(-6 95 128)">
   <g clip-path="url(#la-a)">
    <rect x="12" y="24" width="166" height="208" fill="url(#la-face)"/>
    <circle cx="150" cy="60" r="44" fill="#e8a838" opacity=".24"/>
    <circle cx="95" cy="118" r="38" fill="#e7b463"/>
    <path d="M22 240c3-62 36-90 73-90s70 28 73 90z" fill="#e8a838"/>
    <path d="M58 112c0-28 17-44 38-44 22 0 38 16 38 42-11-14-26-20-41-20-13 0-25 8-35 22z" fill="#1c1a22"/>
   </g>
   <rect x="12" y="24" width="166" height="208" rx="18" stroke="var(--line2)" stroke-width="1.5"/>
  </g>

  <g transform="rotate(5 385 312)">
   <g clip-path="url(#la-b)"><rect x="302" y="208" width="166" height="208" fill="url(#la-star)"/></g>
   <rect x="302" y="208" width="166" height="208" rx="18" stroke="var(--line2)" stroke-width="1.5"/>
  </g>

  <g class="front">
   <g clip-path="url(#la-c)">
    <rect x="118" y="78" width="244" height="305" fill="url(#la-sky)"/>
    <g fill="#fff"><circle cx="150" cy="112" r="1.6"/><circle cx="196" cy="138" r="1.2" opacity=".7"/><circle cx="238" cy="104" r="1.8"/><circle cx="292" cy="128" r="1.3" opacity=".8"/><circle cx="334" cy="100" r="1.6"/><circle cx="318" cy="170" r="1.1" opacity=".6"/><circle cx="160" cy="176" r="1.2" opacity=".6"/></g>
    <circle cx="240" cy="258" r="60" fill="url(#la-sun)"/>
    <path d="M118 306c40-26 76-30 118-12s84 16 126-14v103H118z" fill="#5a2f35"/>
    <path d="M118 334c52-24 92-20 134-4s74 16 110-8v61H118z" fill="#2f1d2a"/>
    <path d="M118 362c60-14 110-12 150-2s62 8 94-4v27H118z" fill="#14111c"/>
   </g>
   <rect x="118" y="78" width="244" height="305" rx="22" stroke="var(--line2)" stroke-width="1.5"/>
  </g>

  <path d="M402 96c2 15 7 20 22 22-15 2-20 7-22 22-2-15-7-20-22-22 15-2 20-7 22-22z" fill="var(--accent)"/>
  <path d="M60 300c1 9 4 12 13 13-9 1-12 4-13 13-1-9-4-12-13-13 9-1 12-4 13-13z" fill="var(--accent)" opacity=".75"/>
 </svg>
</template>
<style scoped>
.art{display:block;width:100%;height:100%;overflow:visible}
.front{filter:drop-shadow(0 16px 26px rgba(0,0,0,.34))}
</style>
