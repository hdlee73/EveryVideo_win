# EveryVideo for Windows

동영상을 재생하고, 구간을 잘라 저장하고, 화면을 캡쳐하는 Windows 동영상 재생기입니다.
안드로이드 앱 [EveryVideo](https://github.com/hdlee73/EveryVideo) 의 Windows 판입니다.

## 내려받기

[Releases](https://github.com/hdlee73/EveryVideo_win/releases/latest) 에서 받습니다.

- `EveryVideo-x.y.z-setup.exe` : 설치 프로그램 (시작 메뉴, 바탕화면 아이콘, "연결 프로그램" 등록)
- `EveryVideo-x.y.z-win-x64-portable.zip` : 설치 없이 압축을 풀어 `EveryVideo.exe` 실행

Windows 10/11 64비트. .NET 을 따로 설치할 필요가 없습니다.

## 기능

| 기능 | 내용 |
|---|---|
| 다양한 포맷 재생 | LibVLC 엔진. MP4, MKV, WebM, AVI, MOV, WMV, FLV, TS/M2TS, MPEG, VOB, 3GP, OGG, RM/RMVB, HLS(m3u8), RTSP, 음악 파일 등. 하드웨어 가속 디코딩 |
| 재생 도구 | 재생 속도(0.25~20배, 1x 버튼·Backspace·오른쪽 클릭으로 바로 원래 속도), 구간반복(A-B, 반복 중에는 그 구간 안에서만 이동), 전체/한 개 반복, 화면 클릭으로 재생/일시정지, 볼륨(최대 200%), 밝기·대비·채도, 화면 비율, 음성·자막 트랙 선택, 자막 파일 불러오기·싱크, 이어서 재생, 항상 위 |
| 캡쳐 / 구간 저장 | 현재 화면을 PNG 로 저장하고 클립보드에도 복사(사진\EveryVideo). 구간을 MP4 로 저장(동영상\EveryVideo, 재생 속도와 상관없이 원래 속도), A-B 를 지정해 두면 자동으로 채워짐 |
| GIF / 썸네일 | 구간을 GIF 로 저장, 지금 화면 또는 장면 모음 썸네일(제목 글자 넣기) |
| 구간 삭제 / 여러 구간 | 구간을 지운 영상 저장, Shift+시간 막대 끌기로 여러 구간을 골라 각각 저장·하나로 합쳐 저장·한꺼번에 삭제 |
| 전체화면 | 두 번 클릭 · F · Enter. 마우스를 움직이면 조작 막대가 나타나고 가만히 두면 숨김 |
| 구글 드라이브 / FTP / SMB | Google Drive for desktop 드라이브(G:)에서 바로 열기(고르지 않고 취소 가능), 공유 링크 붙여넣어 바로 재생, FTP·SMB(윈도우 공유 폴더, NAS) 서버 등록 후 폴더 탐색·스트리밍 |
| 탐색 미리보기 | 시간 막대에 마우스를 올리거나 끌면 그 시점의 작은 미리보기 화면 표시 |
| 이어붙이기 | 여러 동영상을 순서대로 하나의 MP4 로 합치기(끌어서 순서 바꾸기, 정렬), 결과 해상도 선택, 썸네일 그림을 맨 앞에 넣거나 표지로 넣기 |
| 화면 녹화 | 주 모니터 또는 모든 모니터 녹화, 마이크/스테레오 믹스 소리 선택 |
| 즐겨찾기 | 재생 중 특정 시점을 메모와 함께 즐겨찾기, 시간 막대에 ▼ 표시, 다시 열면 옆 패널에서 바로 이동, 모든 동영상의 즐겨찾기 모음 |
| 재생목록 | 옆 패널에 재생목록, 파일·폴더 추가, 끌어서 순서 바꾸기, 이름·날짜·크기순 정렬, × 로 목록에서 빼기, 패널 폭 조절 |
| 확대 | Ctrl+휠로 마우스 위치를 확대(최대 4배), 끌어서 이동 |
| 앱 정보 / 업데이트 | 도움말 › 앱 정보에서 버전과 업데이트 날짜, 만든이, 릴리스 페이지 링크. 시작할 때 새 버전이 있으면 알림 |

단축키는 도움말 › 단축키에서 볼 수 있습니다.

## 빌드 / 릴리스

- 모든 push 에서 GitHub Actions(windows-latest)가 빌드하고 자체 점검(`EveryVideo.exe --selftest`)을 한 뒤 zip 과 설치 파일을 Artifacts 로 올립니다.
- `VERSION` 파일의 버전을 올려 `main` 에 push 하면, 그 버전이 아직 릴리스되지 않았을 때 `v버전` 태그와 GitHub Release 를 만들고 zip 과 설치 파일을 첨부합니다. `v*` 태그를 직접 push 해도 릴리스됩니다.
- FFmpeg 는 빌드할 때 [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) 에서 받아 `ffmpeg\ffmpeg.exe` 로 함께 넣습니다.

로컬 빌드: `dotnet publish src/EveryVideo -c Release -r win-x64 --self-contained` (.NET 8 SDK). 구간 저장·이어붙이기·미리보기·녹화에는 `ffmpeg.exe` 가 실행 파일 옆 `ffmpeg` 폴더나 PATH 에 있어야 합니다.

## 참고

- 구글 드라이브 공유 링크 재생은 "링크가 있는 모든 사용자"로 공유된 파일에서 동작합니다. 내 드라이브의 비공개 파일은 Google Drive for desktop 을 설치해 G: 드라이브에서 여세요.
- 화면 녹화는 Windows 표준 화면 캡쳐를 씁니다. 다른 앱이 보호한(DRM) 화면은 Windows 정책에 따라 검게 녹화될 수 있으며, 이를 우회하는 기능은 넣지 않았습니다.
- 설정과 즐겨찾기는 `%AppData%\EveryVideo` 에 저장됩니다.
- 함께 배포되는 오픈소스는 [THIRD_PARTY.md](THIRD_PARTY.md) 를 보세요.

만든이: 이현덕 (hdlee73@gmail.com)
