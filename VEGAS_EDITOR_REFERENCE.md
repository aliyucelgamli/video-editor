# VEGAS Pro Benzeri Video Editörü — AI Geliştirme Referansı ve Ana Prompt

> Bu doküman, VEGAS Pro 15 ve sonraki sürümlerin çalışma mantığını referans alarak Windows masaüstünde geliştirilecek, hızlı ve anlaşılır bir video/audio editörünün teknik ve UX gereksinimlerini tanımlar.
>
> Kaynak araştırması: VEGAS Pro 15 kullanım kılavuzu ve güncel VEGAS Pro yardım dokümantasyonu. VEGAS'ın güncel dokümantasyonu 15'ten sonraki sürümlerde de aynı temel kavramları doğrulamaktadır.

---

## 1. Ürünün amacı

Windows üzerinde çalışan, VEGAS Pro'dan ilham alan fakat daha sade ve hızlı kullanılabilen bir **non-destructive video + audio editor** geliştirilecek.

Uygulamanın temel kullanım senaryosu:

1. Kullanıcı video/ses/görsel dosyalarını kütüphaneye import eder.
2. Dosyaları timeline'a sürükler.
3. Video, audio, text, image, effect ve transition öğelerini timeline üzerinde düzenler.
4. Timeline'daki değişiklikler kaynak dosyaları değiştirmez.
5. Kullanıcı projeyi kaydeder.
6. İstediği bölümün tamamını veya seçili range'i render/export eder.
7. Proje tekrar açıldığında tüm timeline, efekt, text, keyframe ve medya referansları geri yüklenir.

VEGAS'taki `.veg` mantığında proje dosyası kaynak medya dosyalarının kendisini değiştirmek yerine medya konumlarını ve yapılan editleri saklar. Bu nedenle bizim uygulamamız da **non-destructive editing** kullanmalıdır.

---

# 2. VEGAS Pro arayüz modeli

VEGAS'ın temel UX'i dört ana bölgeye ayrılabilir:

```text
┌────────────────────────────────────────────────────────────────────┐
│ Menu / Toolbar                                                     │
├───────────────────────┬────────────────────────────────────────────┤
│                       │                                            │
│ Media / Explorer /    │              Video Preview                │
│ Effects / Properties  │                                            │
│                       │                                            │
├───────────────────────┴────────────────────────────────────────────┤
│ Timeline / Tracks                                                   │
│                                                                    │
│ V1  [Video Event] [Video Event]                                    │
│ A1  [Audio Event] [Audio Event]                                    │
│ T1  [Text / Image / Overlay]                                      │
│                                                                    │
│ 00:00        00:10        00:20        00:30                      │
└────────────────────────────────────────────────────────────────────┘
```

Bizim uygulamada aynı temel yapı kullanılmalı fakat gereksiz profesyonel menüler ilk aşamada gizlenmeli.

## Önerilen UI

### Üst bar

- New Project
- Open
- Save
- Save As
- Import
- Undo
- Redo
- Cut / Split
- Delete
- Play / Pause
- Stop
- Render / Export
- Settings

### Sol panel

Sekmeli veya dropdown tabanlı:

- Media Library
- Effects
- Transitions
- Text
- Audio
- Templates
- Fonts
- Project Assets

### Orta panel

**Video Preview / Program Monitor**

- Playhead pozisyonunu gösterir.
- Timeline değişikliklerini gerçek zamanlı gösterir.
- Preview quality seçenekleri olabilir:
  - Full
  - Half
  - Quarter
  - Draft

Preview quality düşürülerek karmaşık projelerde daha yüksek playback FPS elde edilebilir.

### Alt panel

**Timeline**

Asıl edit işlemleri burada gerçekleşir.

---

# 3. Timeline mimarisi

VEGAS'ta timeline üzerinde bulunan temel kavram **Track + Event** modelidir.

## Track

Track, timeline üzerinde yatay bir çalışma alanıdır.

Örnek:

```text
V1 ─────────────────────────────────────────
A1 ─────────────────────────────────────────
T1 ─────────────────────────────────────────
```

Track tipi:

- Video
- Audio
- Text / Overlay
- Adjustment / Effects (ileride)

Track üzerinde birden fazla event bulunabilir.

## Event

Event, timeline üzerindeki belirli zaman aralığında bulunan medya instance'ıdır.

Önemli:

**Media File != Event**

Örneğin:

```text
Library:
video.mp4

Timeline:
[video.mp4 00:10-00:20]
[video.mp4 00:30-00:45]
```

Aynı source file timeline'da birden fazla kez kullanılabilir.

Her event'in:

- start time
- duration
- source in
- source out
- playback rate
- volume
- opacity
- fade in
- fade out
- effects
- transitions
- keyframes
- metadata

gibi özellikleri olmalıdır.

---

# 4. Video + Audio ilişkisi

VEGAS'ta audio ve video aynı medya dosyasından geldiğinde ayrı track'lerde event olarak oluşturulabilir.

Bizim uygulamada varsayılan davranış:

```text
V1  [VIDEO EVENT]
A1  [AUDIO EVENT]
```

Bu iki event başlangıçta **grouped / linked** olmalıdır.

Kullanıcı:

- videoyu hareket ettirdiğinde audio da hareket eder.
- videoyu split ettiğinde audio da split edilir.
- audio/video beraber trimlenebilir.

### T tuşu

Kullanıcının isteğine göre:

**T = unlink / separate audio-video**

Ancak VEGAS'ın gerçek shortcut mantığında `T` farklı bir işleve sahip olabilir. Bu nedenle bizim uygulamada kullanıcı tanımlı shortcut sistemi bulunmalıdır.

Önerilen:

```text
T = Separate / Unlink Audio & Video
```

Ayrıldıktan sonra:

```text
V1 [VIDEO]
A1 [AUDIO]
```

bağımsız şekilde hareket ettirilebilir.

---

# 5. Split / Cut sistemi

Kullanıcı isteği:

**X = cut**

Ancak VEGAS'ta varsayılan split shortcut'ı **S**'dir.

Bizim uygulamada iki shortcut desteklenebilir:

```text
S = Split
X = Split
```

Böylece kullanıcı alışkanlığına göre iki seçenek de çalışabilir.

Split işlemi:

```text
Before:

[---------------- VIDEO ----------------]

Cursor
             |

After:

[---------][-----------------------------]
```

Eğer event linked video+audio ise:

```text
V1 [VIDEO A][VIDEO B]
A1 [AUDIO A][AUDIO B]
```

olmalıdır.

Split source dosyasını fiziksel olarak kesmez.

---

# 6. Undo / Redo

Kullanıcı isteği:

```text
Z = Undo
```

Windows/VEGAS tarzı standart:

```text
Ctrl + Z = Undo
Ctrl + Y = Redo
```

olmalıdır.

Ancak kullanıcı deneyimi için:

```text
Z       = Undo
Ctrl+Z  = Undo

Y       = Redo
Ctrl+Y  = Redo
```

desteklenebilir.

## Undo sistemi

Basit bir history stack:

```text
Command History

1. Import video
2. Add video to timeline
3. Split event
4. Move event
5. Add text
6. Apply effect
7. Change volume
```

Her işlem Command Pattern ile modellenmeli.

Örnek:

```text
ICommand
 ├── AddEventCommand
 ├── DeleteEventCommand
 ├── MoveEventCommand
 ├── SplitEventCommand
 ├── TrimEventCommand
 ├── ApplyEffectCommand
 └── ChangePropertyCommand
```

Bu mimari güçlü undo/redo sağlar.

---

# 7. Media Library

VEGAS'ın Project Media penceresi proje içinde kullanılan medyaları toplar.

Bizde:

```text
MEDIA LIBRARY

Videos
  video1.mp4
  video2.mp4

Audio
  music.mp3
  sound.wav

Images
  logo.png
  background.jpg

Text / Templates
  LowerThird
  Title01
```

olmalıdır.

Kullanıcı:

- Import
- Drag & Drop
- Preview
- Rename
- Delete from project
- Search
- Sort
- Tags
- Folders / Bins

yapabilmelidir.

## Önemli

Media Library'den bir dosyayı silmek:

**diskten dosyayı silmemelidir.**

Sadece project reference kaldırılmalıdır.

---

# 8. Media Bin / Folder sistemi

VEGAS'ta medya kategorize edilebilir.

Bizde:

```text
Project
├── Videos
├── Audio
├── Images
├── Fonts
├── Templates
├── Effects
└── User Assets
```

kullanılabilir.

Kullanıcı kendi folder/bin'lerini de oluşturabilmelidir.

Örnek:

```text
Happy Hour
├── Clips
├── Music
├── Memes
├── Players
├── Fonts
└── Templates
```

---

# 9. Import sistemi

Import mümkün olduğunca geniş tutulmalı.

## Video

En az:

- MP4
- MOV
- AVI
- MKV
- WebM

## Audio

- WAV
- MP3
- AAC
- FLAC
- OGG

## Image

- PNG
- JPG/JPEG
- BMP
- WEBP
- TIFF

İleride:

- GIF
- image sequences

## Drag & Drop

Windows Explorer'dan:

```text
file → Media Library
file → Timeline
```

çalışmalıdır.

Bir video timeline'a sürüklendiğinde:

```text
Video Event
+
Audio Event
```

otomatik oluşturulmalıdır.

---

# 10. Trimmer / Source Preview

VEGAS'ın önemli araçlarından biri Trimmer'dır.

Kullanıcı uzun bir videoyu timeline'a koymadan önce:

```text
┌──────────────────────────┐
│      SOURCE PREVIEW      │
│                          │
│      video.mp4           │
│                          │
├──────────────────────────┤
│  [---------range--------]│
│       I          O       │
└──────────────────────────┘
```

ile:

- In point
- Out point

seçebilir.

Sonra:

**Add to Timeline**

ile sadece seçili kısmı timeline'a ekleyebilir.

Bu özellik MVP'de bulunması faydalıdır.

---

# 11. Timeline interaction

Timeline hızlı çalışmalı.

## Mouse

- Drag = move
- Drag edge = trim
- Drag fade handle = fade
- Mouse wheel = zoom
- Middle mouse / space drag = pan
- Double click = edit/open
- Right click = context menu

## Zoom

Timeline:

```text
Zoom Out
<---------------->
Zoom In
```

çok hızlı zoom yapılabilmeli.

Özellikle:

```text
Ctrl + Mouse Wheel
```

veya

```text
Mouse Wheel
```

ile timeline zoom önerilir.

---

# 12. Snap

Timeline'da snap sistemi olmalı.

Örnek:

```text
Snap:
[x] Event edges
[x] Markers
[x] Grid
[x] Playhead
```

Kullanıcı event'i sürüklediğinde yakın event edge'lerine otomatik yapışmalıdır.

---

# 13. Trim sistemleri

En az dört temel edit işlemi:

### Standard Trim

Event'in başlangıç veya sonunu değiştirir.

### Slip

Event'in timeline pozisyonu değişmeden içindeki source footage değiştirilir.

```text
Timeline:
[----------------]

Source:
     <--- visible area --->

Source moves
Timeline duration stays
```

### Slide

Event'in kendisi timeline üzerinde kalırken iki tarafındaki edit noktaları birlikte değiştirilir.

### Stretch

Event duration değişir ve media playback rate değişebilir.

MVP'de:

1. Standard Trim
2. Slip
3. Stretch

yeterlidir.

---

# 14. Fade sistemi

VEGAS event'lerin köşelerinde fade handle kullanır.

Audio:

```text
Audio
████████████████████
╲___________________
 Fade In
```

Video:

```text
Opacity
0% ────────────────> 100%
```

Bizde:

- Audio Fade In
- Audio Fade Out
- Video Fade In
- Video Fade Out

olmalıdır.

Fade curve seçenekleri:

- Linear
- Fast
- Slow
- Smooth
- Exponential

olabilir.

---

# 15. Crossfade

İki event üst üste geldiğinde otomatik crossfade desteklenebilir.

```text
A ────────────────
       ╲
        ╲
         ╲
          B ─────────────
```

Audio crossfade:

```text
A volume ↓
B volume ↑
```

Video crossfade:

```text
A opacity ↓
B opacity ↑
```

---

# 16. Effects sistemi

VEGAS'taki efekt mimarisi birden fazla seviyeye ayrılır.

## Event FX

Sadece seçili event'e uygulanır.

```text
Video Event
    ↓
Color Correction
    ↓
Blur
```

## Track FX

Track üzerindeki tüm event'lere uygulanır.

```text
Video Track
 ├── Clip 1
 ├── Clip 2
 └── Clip 3

Track FX
 └── Color Grade
```

## Media FX

Aynı source media'nın bütün kullanımlarına uygulanır.

```text
video.mp4
 ├── Event A
 ├── Event B
 └── Event C

Media FX
 └── Color Correction
```

## Output FX

Tüm final output'a uygulanır.

```text
Timeline
   ↓
Master / Output FX
   ↓
Render
```

İlk sürümde:

- Event FX
- Track FX
- Output FX

yeterlidir.

---

# 17. Effect categories

Başlangıç efektleri:

## Video

- Brightness
- Contrast
- Saturation
- Hue
- Exposure
- Gamma
- Blur
- Sharpen
- Vignette
- Grayscale
- Sepia
- Color Correction
- Chroma Key
- Glow
- Drop Shadow

## Audio

- Gain
- Volume
- Normalize
- EQ
- Compressor
- Limiter
- Noise Reduction
- Reverb
- Delay

Efekt sistemi plugin-ready tasarlanmalıdır.

---

# 18. Effect Chain

Bir event'e birden fazla efekt uygulanabilir.

```text
EVENT FX

[Brightness]
     ↓
[Contrast]
     ↓
[Blur]
     ↓
[Color Correction]
```

Kullanıcı:

- Add
- Remove
- Reorder
- Enable/Disable
- Duplicate
- Save preset

yapabilmelidir.

---

# 19. Keyframe sistemi

Efekt değerleri zaman içerisinde değişebilmelidir.

Örnek:

```text
0 sec:
Opacity = 0%

2 sec:
Opacity = 100%
```

Keyframe:

```text
●──────────────●
0s             2s
0%            100%
```

Keyframe kullanılabilecek özellikler:

- Position
- Scale
- Rotation
- Opacity
- Volume
- Blur
- Brightness
- Effect parameters
- Text properties

Keyframe interpolation:

- Linear
- Ease In
- Ease Out
- Ease In/Out
- Hold

olmalıdır.

---

# 20. Transform / Crop

Her video/image/text event için:

- Position X/Y
- Scale X/Y
- Rotation
- Anchor/Pivot
- Opacity
- Crop
- Maintain aspect ratio

olmalıdır.

Basit UI:

```text
Position
X: 0
Y: 0

Scale
X: 100%
Y: 100%

Rotation
Z: 0°

Opacity
100%
```

Preview üzerinde doğrudan gizmo ile taşıma/scale/rotate daha sonra eklenebilir.

---

# 21. Track Motion / Parent sistemi

İleri sürümde track'ler parent-child olabilir.

Örneğin:

```text
T1 Parent
 ├── Text
 ├── Image
 └── Video
```

Parent'ın:

- Position
- Scale
- Rotation

değiştiğinde child'lar da hareket eder.

Bu yapı özellikle:

- lower third
- watermark
- title groups
- UI overlays

için kullanılabilir.

---

# 22. Text sistemi

Text ayrı bir timeline event/track olarak desteklenmeli.

Özellikler:

- Font
- Font size
- Bold
- Italic
- Alignment
- Color
- Outline
- Shadow
- Background
- Position
- Scale
- Rotation
- Opacity

Text animasyonu keyframe desteklemeli.

Font sistemi kullanıcı klasöründen font okuyabilmeli.

---

# 23. Image / Overlay sistemi

PNG gibi transparan görseller timeline'a eklenebilmelidir.

Örnek:

```text
V1  Main Video
V2  Logo PNG
V3  Text
```

Katman mantığı:

**üst track = görüntü üzerinde daha üstte render edilir.**

Bu klasik video compositing mantığıdır.

---

# 24. Subtitle sistemi

Altyazı iki şekilde desteklenebilir:

### Timeline Text Events

Her subtitle ayrı text event.

### Subtitle Track

Daha sonra:

```text
SUB1
00:01 Hello
00:03 Welcome
00:05 Let's start
```

şeklinde özel subtitle track yapılabilir.

Import/export:

- SRT
- VTT

MVP için SRT yeterlidir.

---

# 25. Audio editing

Uygulama yalnızca video editor olmamalı.

Audio event:

- Trim
- Split
- Volume
- Fade
- Pan
- Normalize
- Mute
- Solo
- Effects

desteklemeli.

Waveform timeline üzerinde gösterilmelidir.

Örnek:

```text
A1 ──╱╲╱╲╱╲╱╲╱╲╱╲────
```

---

# 26. Audio mixing

İleride:

```text
Audio Tracks
    ↓
Track Volume
    ↓
Track FX
    ↓
Master Bus
    ↓
Output
```

mimarisi kullanılabilir.

MVP:

- track volume
- mute
- solo
- pan
- master volume

yeterlidir.

---

# 27. Markers

Timeline'a marker eklenebilmelidir.

Örnek:

```text
00:05        Marker: Intro
00:12        Marker: Joke
00:30        Marker: Outro
```

Marker:

- name
- time
- color
- comment

tutabilir.

Shortcut:

```text
M = Add Marker
```

---

# 28. Region / Range

Timeline üzerinde bir range seçilebilir:

```text
|=======================|
0s                      10s
```

Bu range:

- preview
- delete
- copy
- render
- export

işlemlerinde kullanılabilir.

Özellikle:

**Render Selected Range**

çok faydalıdır.

---

# 29. Project format

Proje dosyası insan tarafından okunabilir bir format olabilir.

Öneri:

```text
.project.json
```

veya özel:

```text
.myeditor
```

Örneğin:

```json
{
  "project": {
    "name": "Happy Hour Video",
    "width": 1920,
    "height": 1080,
    "fps": 30
  },
  "media": [],
  "tracks": [],
  "events": [],
  "markers": []
}
```

Proje dosyası:

- source path
- media ID
- timeline positions
- trims
- effects
- keyframes
- text
- transitions
- project settings

saklamalıdır.

---

# 30. Missing media

Source dosyanın yeri değişirse uygulama:

```text
Media Offline

video.mp4
C:\OldFolder\video.mp4

[Locate]
[Search Folder]
[Ignore]
```

göstermeli.

Media ID + filename + hash gibi bilgilerle yeniden bağlama yapılabilir.

---

# 31. Proxy editing

Yüksek çözünürlüklü videolarda playback ağır olabilir.

Proxy sistemi:

```text
Original:
4K / 8K

Proxy:
720p / 1080p
```

Preview sırasında proxy kullanılır.

Render sırasında:

```text
Original Media
```

kullanılır.

Bu sistem özellikle büyük videolarda performans için önemlidir.

---

# 32. Caching

Uygulama mümkün olduğunca render sonucunu cachelemelidir.

Örnek:

```text
Project
 ├── Source Media
 ├── Cache
 │    ├── thumbnails
 │    ├── waveforms
 │    ├── proxies
 │    └── preview renders
 └── Project File
```

Cache silinse bile proje bozulmamalıdır.

Cache yeniden üretilebilir olmalıdır.

---

# 33. Preview performance

Preview ile final render birbirinden ayrılmalıdır.

Preview:

- düşük resolution
- proxy
- GPU acceleration
- cached frames

kullanabilir.

Final Render:

- full resolution
- original media
- selected codec
- selected bitrate

kullanmalıdır.

VEGAS da preview quality'nin final render kalitesini değiştirmediğini açıkça ayırır.

---

# 34. Export / Render

Export ekranı:

```text
┌───────────────────────────────┐
│ Export Video                  │
│                               │
│ Format: MP4                   │
│ Codec: H.264                  │
│ Resolution: 1920x1080         │
│ FPS: 30                       │
│ Bitrate: 10 Mbps              │
│ Audio: AAC 48 kHz             │
│                               │
│ Range: Full Project           │
│                               │
│ Output: D:\Exports\video.mp4  │
│                               │
│          [ EXPORT ]            │
└───────────────────────────────┘
```

Başlangıç formatları:

### Video

- MP4 / H.264
- MP4 / H.265
- WebM / VP9 veya AV1

### Audio

- WAV
- MP3
- AAC

### Image

- PNG
- JPG

---

# 35. Export presets

Hazır preset sistemi:

```text
YouTube 1080p
YouTube 4K
Instagram Reels
TikTok
Discord
Custom
```

Preset kullanıcı tarafından kaydedilebilir.

VEGAS'taki render template mantığına benzer şekilde custom export templates oluşturulabilir.

---

# 36. Project Archive

Proje taşınabilir olmalı.

Öneri:

```text
Project
├── project.json
├── Media
├── Fonts
├── Templates
├── Effects
└── Cache
```

ve tek dosya:

```text
MyProject.editorarchive
```

olarak paketlenebilir.

Archive seçeneği:

- Project
- Used Media
- Fonts
- Templates
- Effects
- Optional Cache

içerebilir.

---

# 37. User / Development klasör yapısı

Kullanıcının istediği yapı korunmalı.

```text
App/
│
├── development/
│   ├── src/
│   ├── core/
│   ├── timeline/
│   ├── media/
│   ├── audio/
│   ├── video/
│   ├── rendering/
│   ├── effects/
│   ├── project/
│   ├── ui/
│   ├── commands/
│   ├── utils/
│   └── tests/
│
├── user/
│   ├── players/
│   ├── templates/
│   ├── effects/
│   ├── fonts/
│   ├── music/
│   ├── sounds/
│   ├── images/
│   └── exports/
│
├── cache/
│   ├── thumbnails/
│   ├── waveform/
│   ├── proxy/
│   └── preview/
│
└── projects/
```

**Development klasörü uygulamanın source/code tarafıdır.**

**User klasörü kullanıcı tarafından değiştirilebilir ve güncellenebilir asset alanıdır.**

User klasöründeki içerik update sırasında silinmemelidir.

---

# 38. Template sistemi

Kullanıcı:

```text
user/templates/
```

altına template koyabilir.

Örnek:

```text
templates/
├── LowerThird/
│   ├── template.json
│   └── assets/
│
├── Intro/
│   ├── template.json
│   └── assets/
│
└── MemeText/
    ├── template.json
    └── assets/
```

Template:

- text
- image
- video
- animation
- effects
- keyframes

içerebilir.

---

# 39. Effect preset sistemi

Kullanıcı:

```text
user/effects/
```

içine kendi preset'lerini koyabilir.

Örnek:

```text
effects/
├── Cinematic.json
├── VHS.json
├── MemeZoom.json
└── HappyHour.json
```

Effect preset:

```json
{
  "name": "MemeZoom",
  "effects": [
    {
      "type": "scale",
      "keyframes": []
    },
    {
      "type": "blur",
      "keyframes": []
    }
  ]
}
```

---

# 40. Keyboard shortcuts

Temel kısayollar:

| Shortcut | İşlev |
|---|---|
| Space | Play / Pause |
| J | Reverse playback |
| K | Stop / pause playback |
| L | Forward playback |
| S | Split event |
| X | Split event |
| T | Separate/unlink audio-video |
| Z | Undo |
| Ctrl+Z | Undo |
| Y | Redo |
| Ctrl+Y | Redo |
| Delete | Delete selected |
| Ctrl+C | Copy |
| Ctrl+V | Paste |
| Ctrl+X | Cut |
| Ctrl+A | Select all |
| Ctrl+S | Save |
| Ctrl+Shift+S | Save As |
| Ctrl+O | Open |
| Ctrl+N | New Project |
| M | Add Marker |
| I | Range start |
| O | Range end |
| F2 | Rename |
| +/- | Timeline zoom |
| Mouse Wheel | Timeline zoom |
| Ctrl+Mouse Wheel | Timeline zoom |
| Arrow Left/Right | Move frame |
| Shift+Arrow | Larger timeline movement |

Not: VEGAS Pro'nun gerçek varsayılan shortcut'larında `S = Split`, `J/K/L = playback`, `T = next take`, `Ctrl+Z = Undo` gibi kısayollar bulunur. Bu uygulamada T'nin audio/video unlink olarak kullanılacağı belirtilmiştir; bu nedenle shortcut mapping sistemi **customizable** olmalıdır.

---

# 41. Shortcut customization

Settings içinde:

```text
Settings
 └── Keyboard Shortcuts
      ├── Split
      ├── Separate Audio/Video
      ├── Undo
      ├── Redo
      ├── Play
      ├── Marker
      └── Export
```

Kullanıcı herhangi bir action'a istediği tuşu atayabilmelidir.

Çakışma varsa:

```text
T is already assigned to:
Select Next Take

[Replace]
[Cancel]
```

gibi uyarı çıkmalıdır.

---

# 42. Settings

Settings kategorileri:

## General

- Language
- Autosave
- Autosave interval
- Startup project
- Default project folder

## Video

- Default resolution
- Default FPS
- Preview quality
- Hardware acceleration
- Proxy settings

## Audio

- Sample rate
- Default audio device
- Buffer size

## Rendering

- Default export folder
- Default codec
- Default bitrate
- GPU encoder

## Performance

- RAM cache size
- Proxy generation
- Thumbnail cache
- Preview cache

## Keyboard

- Shortcut editor

---

# 43. Autosave

Otomatik kaydetme:

```text
Every 1 minute
Every 5 minutes
Every 10 minutes
Off
```

olabilir.

Autosave ayrı dosya:

```text
project.autosave
```

olarak tutulabilir.

Crash sonrası:

```text
Recovered Project Found

[Recover]
[Discard]
```

gösterilebilir.

---

# 44. Technology recommendation

## Önerilen teknoloji stack

Windows-first masaüstü uygulama için:

### Language

**C# / .NET 8 veya daha güncel LTS .NET**

### UI

**WPF**

Neden:

- Windows desktop için mature
- Timeline gibi custom UI geliştirmek mümkün
- Hardware-accelerated rendering seçenekleri var
- C# AI coding araçları tarafından iyi destekleniyor
- Büyük proje yapısında maintain edilebilir
- EXE olarak publish edilebilir
- Visual Studio / Rider ile rahat geliştirilebilir

### Media Engine

**FFmpeg**

FFmpeg:

- decoding
- encoding
- transcoding
- audio processing
- frame extraction
- waveform generation
- proxy generation
- export

için kullanılmalı.

### GPU

İleride:

- NVIDIA NVENC
- Intel Quick Sync
- AMD AMF
- DirectX

desteklenebilir.

---

# 45. Neden Electron değil?

Electron + React geliştirme açısından kolaydır ancak gerçek zamanlı video editing için ideal değildir.

Electron:

- yüksek RAM kullanımı
- native video processing entegrasyonu
- büyük timeline'larda performans
- GPU / native media pipeline karmaşıklığı

nedeniyle bu proje için ilk tercih olmamalıdır.

React benzeri UI mimarisi istenirse WPF içinde MVVM kullanılarak component mantığı kurulabilir.

---

# 46. Neden Unity değil?

Bu uygulama oyun olmadığı için Unity kullanılmamalıdır.

Unity:

- video editor timeline için gereksiz runtime overhead
- native file/media pipeline daha karmaşık
- export/editing architecture için uygun değil
- uygulamanın klasik desktop UX'i için ideal değil

Bu nedenle native desktop stack tercih edilmelidir.

---

# 47. Run / Development sistemi

Kullanıcı sürekli build almak istemiyor.

Development sırasında:

```text
run.bat
```

olmalıdır.

Örnek:

```bat
@echo off
dotnet run --project development/src/App/App.csproj
```

Kullanıcı:

```text
run.bat
```

çalıştırınca uygulama açılmalıdır.

Release gerektiğinde:

```text
build.bat
```

kullanılabilir.

Örneğin:

```bat
dotnet publish development/src/App/App.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -o build/
```

Bu şekilde development sırasında sürekli manuel build gerekmemelidir.

---

# 48. Architecture

Önerilen:

```text
App
│
├── UI
│
├── Application
│   ├── Commands
│   ├── UndoRedo
│   └── Services
│
├── Domain
│   ├── Project
│   ├── Track
│   ├── Event
│   ├── Media
│   ├── Effects
│   └── Keyframes
│
├── MediaEngine
│   ├── FFmpeg
│   ├── Decoder
│   ├── Encoder
│   ├── Thumbnail
│   ├── Waveform
│   └── Proxy
│
├── Rendering
│
├── Audio
│
├── ProjectIO
│
└── Infrastructure
```

UI doğrudan FFmpeg komutları çalıştırmamalıdır.

Örneğin:

```text
UI
 ↓
Application Service
 ↓
Media Service
 ↓
FFmpeg
```

olmalıdır.

---

# 49. MVVM

WPF tarafında:

```text
View
 ↓
ViewModel
 ↓
Service
 ↓
Domain
```

kullanılmalıdır.

Örneğin:

```text
TimelineView
TimelineViewModel
TimelineService
Project
Track
Event
```

Timeline UI state'i ile gerçek project data birbirinden ayrılmalıdır.

---

# 50. Domain modeli

Temel model:

```text
Project
 ├── ProjectSettings
 ├── MediaLibrary
 ├── Tracks
 │    ├── VideoTrack
 │    ├── AudioTrack
 │    └── OverlayTrack
 ├── Markers
 └── Metadata
```

Track:

```text
Track
 ├── Id
 ├── Name
 ├── Type
 ├── Events
 ├── Volume
 ├── Opacity
 ├── Muted
 ├── Solo
 ├── Effects
 └── ParentId
```

Event:

```text
Event
 ├── Id
 ├── MediaId
 ├── Start
 ├── Duration
 ├── SourceIn
 ├── SourceOut
 ├── PlaybackRate
 ├── FadeIn
 ├── FadeOut
 ├── Effects
 ├── Keyframes
 ├── Transform
 └── LinkedEventId
```

---

# 51. Rendering architecture

Timeline renderer ile export renderer ayrılmalı.

```text
Timeline
   ↓
Composition Graph
   ↓
Preview Renderer
```

ve:

```text
Timeline
   ↓
Composition Graph
   ↓
Export Renderer
   ↓
FFmpeg Encoder
```

aynı project modelini kullanmalı.

Böylece preview ile final render farklı sonuç üretmemeli.

---

# 52. Composition Graph

Render sırasında:

```text
Video Track 3
      ↓
Video Track 2
      ↓
Video Track 1
      ↓
Audio Tracks
      ↓
Master
      ↓
Output
```

oluşturulmalı.

Her frame için:

```text
Source
→ Trim
→ Playback Rate
→ Transform
→ Opacity
→ Event FX
→ Track FX
→ Compositing
→ Output FX
→ Final Frame
```

işlenebilir.

---

# 53. Non-destructive editing zorunluluğu

Kaynak medya asla doğrudan değiştirilmemeli.

Örneğin:

```text
video.mp4
```

dosyası split edildiğinde yeni:

```text
video_part1.mp4
video_part2.mp4
```

oluşturulması gerekmemelidir.

Sadece project:

```text
Event A:
sourceIn = 0
sourceOut = 10

Event B:
sourceIn = 10
sourceOut = 20
```

saklamalıdır.

Bu, VEGAS'ın temel çalışma mantığıdır.

---

# 54. MVP

İlk sürümde her şeyi yapmaya çalışma.

## MVP özellikleri

### Project

- New
- Open
- Save
- Save As
- Autosave

### Media

- Import video
- Import audio
- Import image
- Media Library
- Drag & Drop

### Timeline

- Video track
- Audio track
- Overlay track
- Add event
- Move
- Trim
- Split
- Delete
- Snap
- Zoom
- Pan
- Group / unlink

### Preview

- Play
- Pause
- Seek
- Frame step

### Audio

- Waveform
- Volume
- Fade
- Mute
- Solo

### Video

- Fade
- Opacity
- Position
- Scale
- Rotation

### Text

- Add text
- Font
- Size
- Color
- Position

### Export

- MP4 H.264
- WAV
- PNG/JPG
- Custom resolution
- FPS
- Bitrate

### Undo

- Command history
- Undo
- Redo

---

# 55. MVP sonrası

## Phase 2

- Effects
- Transitions
- Keyframes
- Audio FX
- Subtitle
- SRT import/export
- Templates
- Effect presets
- Markers
- Regions
- Proxy

## Phase 3

- GPU acceleration
- Hardware encoding
- Nested timelines
- Parent tracks
- Adjustment tracks
- Advanced color correction
- Chroma key
- Advanced audio mixer

## Phase 4

- Plugin API
- VST
- Custom effects
- Script API
- Project archive
- Collaboration features

---

# 56. UX prensipleri

Uygulama VEGAS'ın gücünü korurken daha basit görünmelidir.

## Ana prensipler

1. Her şey mümkün olduğunca drag & drop.
2. Kullanıcı timeline'da ne olduğunu hemen anlamalı.
3. Gizli state minimum olmalı.
4. Sağ click menüsü güçlü olmalı.
5. Keyboard shortcuts hızlı edit için optimize edilmeli.
6. Preview her zaman görünür olmalı.
7. Timeline ana çalışma alanı olmalı.
8. Uzun modal pencerelerden kaçınılmalı.
9. Ağır işlemlerde progress göstergesi olmalı.
10. UI thread bloklanmamalı.

---

# 57. Performance requirements

Uygulama:

- UI thread'i uzun süre bloklamamalı.
- FFmpeg işlemleri background process/thread olarak çalışmalı.
- Thumbnail generation async olmalı.
- Waveform generation async olmalı.
- Proxy generation async olmalı.
- Preview cache kullanılmalı.
- Büyük timeline'larda yalnızca görünür alan render edilmeli.
- Timeline virtualization kullanılmalı.
- Media metadata cache'lenmeli.

Özellikle 1000+ event içeren timeline'da UI donmamalıdır.

---

# 58. Error handling

FFmpeg hataları kullanıcıya ham console çıktısı olarak gösterilmemeli.

Örneğin:

```text
Export Failed

The video could not be encoded.

Possible reasons:
• Unsupported codec
• Missing source media
• Not enough disk space

[Show Details]
```

Teknik log:

```text
logs/
 └── app.log
```

içine yazılabilir.

---

# 59. AI ile geliştirme kuralları

AI kod yazarken:

- Önce architecture'ı bozma.
- UI ile domain logic'i karıştırma.
- FFmpeg komutlarını UI içine yazma.
- Global static state'i minimum kullan.
- Her edit işlemini command olarak modelle.
- Undo/redo her command üzerinden çalışmalı.
- Async işlemlerde UI thread bloklanmamalı.
- Dosya yolları hard-code edilmemeli.
- User asset klasörleri configuration üzerinden belirlenmeli.
- Project format backward-compatible düşünülmeli.
- Büyük class oluşturma.
- SOLID uygulanmalı.
- Her feature ayrı service/modül olmalı.
- Kod eklemeden önce mevcut architecture incelenmeli.
- Mevcut sistemi gereksiz yere yeniden yazma.
- Bir feature tamamlanmadan başka feature'a geçme.

---

# 60. Ana AI Development Prompt

Aşağıdaki prompt proje root'unda bulunan bu dokümanla birlikte kullanılmalıdır:

```text
We are building a Windows desktop non-linear video and audio editor inspired by Sony VEGAS Pro 15+.

The application must be fast, clean, understandable, and optimized for timeline-based editing.

Read and follow the project's VEGAS_EDITOR_REFERENCE.md before implementing features.

IMPORTANT:
Do not blindly copy the entire VEGAS interface. Use VEGAS Pro as the UX and editing-model reference, but simplify the UI and make the application easier to use.

TECHNOLOGY:
- Windows desktop application
- C# / .NET 8+ LTS
- WPF
- MVVM
- FFmpeg for media decoding, encoding, transcoding, thumbnails, waveform generation and proxy generation
- Use async/background processing for heavy operations
- Keep UI responsive at all times
- Development must be runnable without repeatedly building an EXE
- Provide run.bat for development
- Provide build.bat for release builds

CORE ARCHITECTURE:

UI
→ ViewModels
→ Application Services
→ Domain
→ Media/Rendering Infrastructure
→ FFmpeg

Do not put FFmpeg commands directly into UI code.

The project must use a clean modular architecture.

CORE CONCEPTS:

Project
MediaLibrary
Track
Event
Effect
Transition
Keyframe
Marker
Region
Command
Undo/Redo

The application must use NON-DESTRUCTIVE editing.

Source media files must NEVER be modified by timeline editing.

A media file is a source asset.
An Event is an instance of that media on the timeline.

Example:

One video.mp4 can be used multiple times:

Event A:
sourceIn = 0
sourceOut = 10

Event B:
sourceIn = 30
sourceOut = 45

Do not create new physical video files for normal editing operations such as split or trim.

TIMELINE:

The main UI must contain a timeline similar in workflow to VEGAS.

Tracks:
- Video
- Audio
- Overlay/Text/Image

Tracks contain Events.

Video files containing audio should normally create:
- one video event
- one linked audio event

The audio/video events should initially move and edit together.

CUSTOM SHORTCUTS:

Space = Play/Pause
J = Reverse playback
K = Stop/Pause
L = Forward playback
S = Split
X = Split
T = Separate/Unlink Audio + Video
Z = Undo
Ctrl+Z = Undo
Y = Redo
Ctrl+Y = Redo
Delete = Delete
Ctrl+C = Copy
Ctrl+V = Paste
Ctrl+X = Cut
Ctrl+A = Select All
Ctrl+S = Save
Ctrl+Shift+S = Save As
Ctrl+O = Open
Ctrl+N = New Project
M = Marker
I = Range Start
O = Range End

Do not hard-code shortcuts permanently.
Implement a customizable keyboard shortcut system.

TIMELINE INTERACTION:

- Drag event = move
- Drag event edge = trim
- Drag fade handle = fade
- Mouse wheel / Ctrl+mouse wheel = zoom
- Pan timeline
- Snap to event edges
- Snap to markers
- Snap to playhead
- Split events at playhead
- Group linked audio/video
- Unlink audio/video
- Copy/paste events
- Duplicate events
- Multi-select events

The timeline must remain responsive even with many events.

VIDEO:

Support:
- MP4
- MOV
- AVI
- MKV
- WebM

Images:
- PNG
- JPG/JPEG
- BMP
- WEBP
- TIFF

VIDEO EVENT PROPERTIES:

- Position
- Scale
- Rotation
- Opacity
- Crop
- Playback rate
- Fade in
- Fade out
- Effects
- Keyframes

A video event should be rendered through:

Source
→ Trim
→ Playback Rate
→ Transform
→ Opacity
→ Event FX
→ Track FX
→ Compositing
→ Output FX

AUDIO:

Support:
- WAV
- MP3
- AAC
- FLAC
- OGG

Audio features:
- Waveform
- Volume
- Fade in
- Fade out
- Pan
- Mute
- Solo
- Normalize
- Audio FX

AUDIO/VIDEO LINKING:

When a video and its original audio are imported together, create a relationship between them.

Splitting a linked event should split both.

Moving one should move both.

T should unlink them.

MEDIA LIBRARY:

Provide:
- Import
- Drag & Drop
- Preview
- Search
- Rename
- Tags
- Folders/Bins
- Delete from project

Deleting from Media Library must NOT delete the original disk file.

USER ASSETS:

Separate development data from user-editable assets.

Recommended structure:

App/
├── development/
│   ├── src/
│   ├── core/
│   ├── timeline/
│   ├── media/
│   ├── audio/
│   ├── video/
│   ├── rendering/
│   ├── effects/
│   ├── project/
│   ├── ui/
│   ├── commands/
│   ├── utils/
│   └── tests/
│
├── user/
│   ├── players/
│   ├── templates/
│   ├── effects/
│   ├── fonts/
│   ├── music/
│   ├── sounds/
│   ├── images/
│   └── exports/
│
├── cache/
│   ├── thumbnails/
│   ├── waveform/
│   ├── proxy/
│   └── preview/
│
└── projects/

Do not overwrite/delete user assets during application updates.

EFFECT SYSTEM:

Implement a plugin-ready effect architecture.

Effects can eventually exist at:
- Event level
- Track level
- Media level
- Output level

MVP should prioritize Event FX and Track FX.

Initial video effects:
- Brightness
- Contrast
- Saturation
- Hue
- Exposure
- Gamma
- Blur
- Sharpen
- Vignette
- Grayscale
- Sepia
- Color Correction
- Chroma Key
- Glow
- Drop Shadow

Initial audio effects:
- Gain
- EQ
- Compressor
- Limiter
- Normalize
- Reverb
- Delay
- Noise Reduction

Effects must support:
- Enable/Disable
- Remove
- Reorder
- Presets
- Keyframes where applicable

KEYFRAMES:

Properties must be animatable over time.

Examples:
- Position
- Scale
- Rotation
- Opacity
- Volume
- Blur
- Effect parameters

Interpolation:
- Linear
- Ease In
- Ease Out
- Ease In/Out
- Hold

TEXT:

Provide text events with:
- Font
- Size
- Bold
- Italic
- Alignment
- Color
- Outline
- Shadow
- Background
- Position
- Scale
- Rotation
- Opacity

Fonts should be discoverable from user/fonts/.

SUBTITLES:

Support SRT import/export.

Initially subtitles may be represented as text events.
Later create a dedicated subtitle track.

EXPORT:

Use FFmpeg.

Initial export:
- MP4 H.264
- MP4 H.265
- WAV
- MP3
- PNG/JPG

Export dialog should support:
- Format
- Codec
- Resolution
- FPS
- Bitrate
- Audio settings
- Output path
- Full project / selected range

Provide presets:
- YouTube 1080p
- YouTube 4K
- Instagram Reels
- TikTok
- Discord
- Custom

PROJECT:

Use a JSON-based project format initially.

Example:

{
  "project": {},
  "media": [],
  "tracks": [],
  "events": [],
  "markers": []
}

The project must store references and edit information, not source media copies.

Implement:
- New
- Open
- Save
- Save As
- Autosave
- Recovery
- Missing media detection
- Relink media

UNDO/REDO:

All editing operations must be represented as commands.

Use:

ICommand
- AddEventCommand
- DeleteEventCommand
- MoveEventCommand
- SplitEventCommand
- TrimEventCommand
- ApplyEffectCommand
- ChangePropertyCommand

Undo/redo must restore project state correctly.

PREVIEW:

Preview and final rendering must use the same project/composition model.

Preview may use:
- proxy files
- lower resolution
- cached frames
- GPU acceleration

Final render must use original media.

Do not allow preview-only logic to produce a different composition from final render.

PERFORMANCE:

- Never block the UI thread with FFmpeg.
- Thumbnail generation must be asynchronous.
- Waveform generation must be asynchronous.
- Proxy generation must be asynchronous.
- Export must be asynchronous.
- Cache generated data.
- Virtualize timeline events where practical.
- Render only visible timeline content when possible.
- Keep 1000+ timeline events usable.

ERROR HANDLING:

Do not show raw FFmpeg errors as the primary user experience.

Show:
- friendly error
- possible reasons
- Show Details button

Write technical details to logs/app.log.

DEVELOPMENT:

Create:

run.bat

which starts the app using dotnet run.

Example:

dotnet run --project development/src/App/App.csproj

Create:

build.bat

for release publishing.

Do not require a new EXE build after every code change during development.

IMPLEMENTATION PROCESS:

Before implementing a feature:

1. Inspect the existing architecture.
2. Identify affected modules.
3. Explain the implementation plan briefly.
4. Implement the smallest clean solution.
5. Do not rewrite unrelated systems.
6. Preserve existing behavior.
7. Add/update tests where practical.
8. Verify that the project still runs.
9. Keep code modular and maintainable.

Do not create giant classes.

Follow SOLID principles.

Keep UI, domain, media processing and infrastructure separated.

When a requirement is ambiguous, prefer the behavior described in VEGAS_EDITOR_REFERENCE.md and the project's existing architecture.

The final goal is not to recreate every VEGAS feature.

The goal is:

"VEGAS-style non-linear editing workflow with a simpler UI, fast timeline interaction, strong keyboard editing, easy media management, and a clean architecture that can grow into a professional video/audio editor."

Start with the MVP.
Do not implement advanced features before the core timeline, project model, media import, preview, undo/redo and export are stable.
```

---

# 61. Important implementation note

VEGAS'ın gerçek davranışını birebir kopyalamak yerine **editing modelini** referans almak daha doğru olacaktır.

Özellikle şu kavramlar korunmalı:

```text
Media
↓
Event
↓
Track
↓
Timeline
↓
Compositing
↓
Output
```

Bu model uygulamanın temelini oluşturmalıdır.

---

# 62. Kaynaklar

- VEGAS Pro 15 User Manual — timeline, media, Trimmer, editing, project ve workflow referansı.
- VEGAS Pro Help — Project Media, effects, keyframes, track motion, rendering, project files ve keyboard shortcuts.
- VEGAS Pro 21/22/23 Help — güncel editing modelinin ve temel mimarinin doğrulanması.

Önemli kaynak başlıkları:

- Project Media
- Editing Events on the Timeline
- Keyboard Shortcuts
- Keyframe Animation
- Applying Effects
- Rendering Projects
- Project Properties
- Track Motion
- Nested Timelines
- Project Archiving

---

# 63. Araştırmadan çıkarılan kritik sonuçlar

1. **Media ve Event ayrı kavramlar olmalı.**
2. **Editing non-destructive olmalı.**
3. **Timeline Track + Event modeliyle kurulmalı.**
4. **Audio ve video başlangıçta linked olmalı.**
5. **Effects farklı seviyelerde uygulanabilecek şekilde tasarlanmalı.**
6. **Keyframe sistemi baştan architecture'a dahil edilmeli.**
7. **Preview ve final render aynı composition modelini kullanmalı.**
8. **Proxy/cache sistemi büyük dosyalarda performans için önemli.**
9. **Project dosyası source media yerine referans + edit state saklamalı.**
10. **Undo/redo command architecture ile yapılmalı.**
11. **Keyboard shortcuts kullanıcı tarafından değiştirilebilir olmalı.**
12. **FFmpeg medya pipeline'ın merkezinde olmalı.**
13. **UI thread hiçbir ağır medya işleminde bloklanmamalı.**
14. **User assets ile development/source code ayrılmalı.**
15. **İlk sürümde timeline + media + preview + export stabil hale gelmeden advanced effects/plugin sistemi yapılmamalı.**

---

# 64. İlk geliştirme sırası

AI'a tüm sistemi tek seferde yaptırmak yerine şu sırayla ilerlemek daha güvenlidir:

```text
1. Project model
2. Media model
3. Track/Event model
4. Project save/load
5. Basic WPF UI
6. Media Library
7. Timeline
8. Drag & Drop
9. Move/Trim
10. Split
11. Audio/Video linking
12. Undo/Redo
13. Preview
14. Waveform
15. Fade
16. Text
17. Transform
18. FFmpeg export
19. Autosave
20. Missing media
21. Effects
22. Keyframes
23. Transitions
24. Proxy
25. Advanced audio
26. Templates
27. Subtitle
28. GPU optimization
```

Bu sıra, temel editörün önce kullanılabilir hale gelmesini ve daha sonra profesyonel özelliklerin eklenmesini sağlar.
