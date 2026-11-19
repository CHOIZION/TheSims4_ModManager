# Sims 4 Mod Auto Updater

`.NET 10` & `WPF` 기반의 **The Sims 4 모드/CC 분석·분류·업데이트 자동화 데스크톱 애플리케이션**입니다.  
게임 패키지 바이너리 포맷(DBPF 2.1, STBL, `.ts4script` ZIP/`.pyc`)을 외부 네이티브 도구 없이 순수 C#으로 파싱하여 제작자 추론, 기능별 분류, 배포처 업데이트 확인, 안전 백업/복원을 지원합니다.

---

## 주요 기능

- **바이너리 메타데이터 추출 (`ModMetadataExtractor`)**
  - `.package` (DBPF 2.1 헤더/인덱스, zlib 압축 청크 해제, `ModFileManifest` XML, CASP/NameMap, THUM PNG/JPEG 썸네일) 직접 파싱
  - DBPF `STBL` 문자열 테이블의 16진수 해시 키(`0x...`) 지연 디코딩을 통한 제작자/표시명 복원
  - `.ts4script` 아카이브의 `ModFileManifest.yml`, 루트 모듈명, `.pyc` 마샬 바이트코드 내부 버전/URL 추출
- **유형별 4대 기능 분류 (`유형별 분류` 탭)**
  - `ScriptCoreMod` (`.ts4script` 기반 핵심 스크립트 모드)
  - `XmlGameplayMod` (CASP/COBJ 없는 XML 튜닝 및 STBL 번역 팩)
  - `ObjectModAndCc` (작동 기능 오브젝트, CAS/가구 CC, 오디오/텍스처/슬라이더)
  - `AnimationAndPose` (CLIP/포즈 팩/애니메이션 스니펫 리소스)
- **모드 탐색기 & 제작자 정리 (`CreatorOrganizerService`, `DuplicateCleanupService`)**
  - 파일명/폴더명/내부 튜닝 네임스페이스/URL 도메인/외부 오버라이드(`creator_overrides.json`) 기반 다단계 제작자 추론
  - 동일 파일명·동일 내용(SHA-256) 중복 감지 및 우선순위 기반 일괄 삭제(`DeleteDuplicatesDialog`, 사전 백업)
  - 전체 제작자별 정리, 특정 지정 폴더(예: `Mods\test`) 기존 폴더 통합 정리(빈 원본 폴더 자동 삭제), 미분류 전용 `Mods\Unknown` 이동, 한글패치(`!WAMTIK`·`PersonalKO`) `Overrides` 고정 및 `Resource.cfg` 보호
- **업데이트 센터 (`ModUpdaterService`, `ModFinderService`)**
  - 기본 조회 범위를 핵심 `.ts4script` 모드로 스코핑하고 필요 시 `핵심 + XML` 또는 `전체 모드`로 전환
  - 내장 카탈로그(`known_catalog.json`), 파일 내장 URL, 웹 검색 랭킹 3단계 배포처 탐지 및 URL 피닝(`mods_manifest.json`)
  - 시맨틱 버전, 날짜 형식, 알파벳 접미사(`v60a`/`v60b`), 한글화 패치 버전 등 복합 버전 비교 + Range/CRC32 무결성 검증
- **트랜잭션 백업 & Zip-Slip 방어 (`BackupService`, `ArchiveAndFileService`)**
  - 설치 전 세션 단위 자동 백업 및 원클릭 복원
  - 압축 해제 시 경로 탈출(Zip-Slip) 검증, 스테이징 임시 디렉터리 교체

---

## 프로젝트 구조

```text
Sims4ModUpdater.slnx
├─ src/
│  ├─ Sims4ModUpdater.Core/     # 바이너리 파서, 스캐너, 분류기, 업데이트/백업 핵심 엔진 (.NET 10)
│  └─ Sims4ModUpdater.App/      # WPF MVVM 데스크톱 UI (MainViewModel, Views, DarkTheme)
└─ tests/
   └─ Sims4ModUpdater.Tests/    # 핵심 서비스/파서 자동화 테스트 스위트 (14개 테스트 케이스)
```

---

## 실행 및 빌드 방법 (Windows)

### 요구 사항
- Windows 10 / 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/) (개발/테스트 빌드 시 필요; Release 단일 `.exe`는 런타임 포함 self-contained 빌드로 동작)

### 개발 모드 실행
```bat
Run-Dev.cmd
```
또는 PowerShell:
```powershell
.\Run-Dev.ps1
```

### 자동화 테스트 실행
```bat
Run-Tests.cmd
```

### Release 단일 실행 파일(`.exe`) 빌드
```bat
Build-Release.cmd
```
빌드 완료 후 `release\win-x64\Sims4ModUpdater.exe`가 생성됩니다.
