# Video Editor

VEGAS Pro'dan ilham alan, Windows için non-destructive video/audio editörü.
Teknik referans: [VEGAS_EDITOR_REFERENCE.md](VEGAS_EDITOR_REFERENCE.md)

## Gereksinimler

- Windows 10/11
- [.NET 10 SDK x64](https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.exe)

## Çalıştırma

```bat
run.bat      :: Uygulamayı development modunda başlatır (build gerekmez)
test.bat     :: Test paketini çalıştırır
build.bat    :: build\ altına self-contained release EXE üretir
```

## Yapı

```
development/          Kaynak kod (kullanıcı dosyalarından ayrı)
  src/Domain/         Proje modeli: Project, Track, TimelineEvent, MediaItem, Keyframe...
  src/Application/    Servisler + Command pattern undo/redo (IEditorCommand)
  src/ProjectIO/      JSON tabanlı .veproj proje formatı (versiyonlu)
  src/App/            WPF arayüzü (MVVM)
  tests/Tests/        Sıfır bağımlılıklı test paketi
user/                 Kullanıcı asset'leri (font, template, efekt preset...) — update'te silinmez
cache/                Yeniden üretilebilir cache (thumbnail, waveform, proxy, preview)
projects/             Proje dosyaları için önerilen konum
```

## Mimari

```
UI (WPF/MVVM) → Application Services → Domain → MediaEngine/FFmpeg (henüz eklenmedi)
```

Temel ilkeler: non-destructive editing (kaynak dosyalara asla dokunulmaz),
Media ≠ Event ayrımı, her edit işlemi undo edilebilir bir IEditorCommand,
preview ve final render aynı composition modelini kullanacak.

## Durum (iskelet — geliştirme sırası 1-5 tamam)

- [x] Domain modeli (Track + Event, keyframe/effect/transform alanlarıyla)
- [x] Command pattern undo/redo (Split dahil)
- [x] .veproj kaydet/yükle (JSON, format versiyonlu, atomik yazma)
- [x] WPF shell: toolbar, Media Library, preview alanı, timeline görünümü
- [x] Media import (dosya referansı olarak; süre/thumbnail için FFmpeg bekliyor)
- [ ] Sıradaki: timeline'a sürükle-bırak, move/trim etkileşimi, FFmpeg entegrasyonu
