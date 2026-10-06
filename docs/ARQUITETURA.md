# G-HOSP Panel Watchdog — Análise e Arquitetura

Este documento registra a análise feita **antes** da implementação (requisito 44):
dificuldades técnicas, decisões e o porquê de cada uma.

---

## 1. Escolha de tecnologia

| Opção | Avaliação |
|---|---|
| **C# / .NET 10 + WinForms** ✅ | `NotifyIcon` nativo, P/Invoke direto para Win32/Core Audio, sem dependências externas. Processo ocioso fica em ~25–40 MB de RAM e ~0% de CPU. |
| WPF | Mais pesado (DirectX, árvore visual). A UI aqui é mínima (bandeja + 2 telas de admin), então WPF não traz ganho. |
| C++ puro | Consumo menor ainda, mas custo de manutenção e risco de bugs de memória maiores. O ganho (~20 MB) não compensa. |
| PowerShell/AutoHotkey | Frágeis, difíceis de manter, sem tratamento de eventos COM confiável. |

**Zero pacotes NuGet.** Toda a interop (Core Audio, DisplayConfig, Credential Manager,
CreateProcess, NtQueryInformationProcess) é declarada à mão. O protocolo do Edge (CDP)
usa `ClientWebSocket` + `System.Text.Json`, ambos do próprio .NET.

Configurações de runtime para baixo consumo: GC de estação de trabalho **não concorrente**
(uma thread a menos), `InvariantGlobalization` (não carrega ICU) e nenhum timer de alta frequência.

---

## 2. Como diferenciar o Edge do painel do Edge do funcionário

O Chromium garante **um processo "browser" por `--user-data-dir`**. O painel usa um perfil exclusivo:

```
%LOCALAPPDATA%\GHospPanelWatchdog\EdgeProfile
```

Identificação (nunca pelo nome `msedge.exe` sozinho):

1. Lista processos `msedge.exe`.
2. Para cada um, lê a **linha de comando** via `NtQueryInformationProcess(ProcessCommandLineInformation)`
   (barato, sem WMI).
3. O processo do painel é aquele cuja linha de comando contém `--user-data-dir=<nosso perfil>`
   **e não** contém `--type=` (ou seja, é o processo principal, não um renderer/GPU).
4. O PID encontrado fica em cache; um evento `Process.Exited` avisa imediatamente quando ele morre.
   A varredura completa só ocorre quando o PID ainda não é conhecido.
5. A janela do painel = janela visível de nível superior, classe `Chrome_WidgetWin_1`,
   pertencente a esse PID.

Para encerrar (travamento/reinício): mata **somente** a árvore de processos desse PID
(`Process.Kill(entireProcessTree)`) e, por segurança, qualquer `msedge.exe` remanescente cuja
linha de comando contenha nosso perfil. O Edge do funcionário usa outro perfil/árvore e nunca é tocado.

---

## 3. Controle do Edge: CDP (Chrome DevTools Protocol), sem Playwright/WebDriver

Playwright ou WebDriver exigiriam Node/driver externo (+100–200 MB) e manteriam processos pesados.
Em vez disso, o Edge do painel é iniciado com `--remote-debugging-port=0`:

* o Edge escolhe uma porta livre, ouve **somente em 127.0.0.1** e grava a porta em
  `EdgeProfile\DevToolsActivePort`;
* o watchdog abre **um único WebSocket** para o Edge (custo desprezível) e o usa para:
  ler URL/estado da página, detectar tela de login, preencher o login, navegar/recarregar,
  detectar travamento do renderer e colocar a janela em tela cheia.

Nenhum processo extra fica rodando. O WebSocket é refeito automaticamente se cair.

> Segurança: a porta de depuração aceita conexões apenas locais e é aleatória. Qualquer processo
> do mesmo usuário Windows poderia, em teoria, conectar-se a ela — o mesmo nível de confiança que
> esse processo já teria sobre os cookies do perfil no disco. Está documentado no README.

### Argumentos do Edge do painel

```
--user-data-dir="...\EdgeProfile"       perfil exclusivo
--app="https://painel.g-hosp.com.br/..."  modo aplicativo: sem barra de endereço/abas
--remote-debugging-port=0                CDP em porta aleatória local
--autoplay-policy=no-user-gesture-required   o som de chamada toca sem clique
--no-first-run --no-default-browser-check
--hide-crash-restore-bubble --disable-session-crashed-bubble
--disable-background-timer-throttling --disable-renderer-backgrounding
--disable-backgrounding-occluded-windows  o painel nunca é "congelado"
--disable-extensions --disable-sync --disable-background-mode
--window-position / --window-size        já nasce dentro da TV
```

Antes do primeiro uso, o arquivo `Preferences` do perfil é semeado com o gerenciador de senhas e
o tradutor desligados (para não surgir o balão "Salvar senha?" na TV).

---

## 4. Monitor secundário (TV)

* `EnumDisplayMonitors` + `GetMonitorInfo` → retângulos reais em pixels físicos, qual é o principal.
* `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo` → nome amigável (ex.: "SAMSUNG"),
  tipo de conexão (HDMI/DP) e o **caminho do dispositivo** (`\\?\DISPLAY#...`), que é estável
  entre reconexões e reinicializações. É esse caminho que fica salvo na configuração.
* O processo é **Per-Monitor DPI Aware V2**, então todas as coordenadas são físicas e coerentes
  com as do Edge, mesmo com escalas diferentes (ex.: monitor 125% e TV 100%).
* Modos: **Automático** (primeiro monitor não principal) ou **Específico** (escolhido na lista).
  Com 3+ monitores o administrador escolhe pela bandeja ou nas configurações.
* Nada de coordenadas fixas, nada de resolução assumida, nunca altera o monitor principal.

### Eventos
`SystemEvents.DisplaySettingsChanged` (WM_DISPLAYCHANGE), retomada de suspensão e desbloqueio
de sessão disparam uma verificação imediata + rechecagens em 2 s e 6 s (o Windows demora a
estabilizar a topologia após um HDMI voltar). Fora disso, a verificação periódica (5 s) é o fallback.

### TV desconectada
Quando o HDMI sai, **o Windows move as janelas da TV para o monitor principal** — o painel em tela
cheia cobriria o trabalho do funcionário. Por isso, ao detectar que a TV sumiu, o watchdog
**minimiza a janela do painel sem ativá-la** (`SW_SHOWMINNOACTIVE`) e fica aguardando, sem
mensagens. Se a TV não está presente, o watchdog **não abre** o painel (ele abriria no monitor do
funcionário). Quando a TV volta: restaura sem ativar, move para a TV e coloca em tela cheia.

---

## 5. Posicionar e "maximizar" sem roubar foco

`ShowWindow(SW_MAXIMIZE)` **ativa** a janela (rouba foco). Por isso:

1. O Edge é lançado com `CreateProcess` + `STARTUPINFO.wShowWindow = SW_SHOWNOACTIVATE`;
   o primeiro `ShowWindow` do Edge vira "mostrar sem ativar". Além disso, o watchdog roda em
   segundo plano, e o Windows (foreground lock) impede que um processo lançado por um processo
   que não está em primeiro plano tome o foco.
2. Mover para a TV: `SetWindowPos(..., SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER)`.
3. Ocupar toda a TV: `Browser.setWindowBounds { windowState: "fullscreen" }` via CDP.
   A implementação de tela cheia do Chromium no Windows usa `SWP_NOACTIVATE`, cobre o monitor
   inteiro (inclusive sem barra de título) e fica no monitor onde a janela está — a TV.
4. Fallback sem CDP: `SetWindowPos` com o retângulo completo da TV, também `SWP_NOACTIVATE`.
5. Restaurar de minimizado: `SetWindowPlacement` com `SW_SHOWNOACTIVATE`.
6. Guarda de foco: o watchdog anota a janela em primeiro plano antes de lançar o Edge; se mesmo
   assim o painel aparecer em primeiro plano, tenta devolver o foco à janela anterior e registra no log.

Não há movimento de mouse, clique simulado, teclado simulado ou pop-up.

---

## 6. Áudio "Multi"

| Necessidade | API |
|---|---|
| Listar saídas, ler nome e **ID persistente** | `IMMDeviceEnumerator` / `IMMDevice` / `IPropertyStore` (documentadas) |
| Saber o padrão atual | `GetDefaultAudioEndpoint(eRender, role)` |
| Trocar o padrão | `IPolicyConfig::SetDefaultEndpoint` |
| Ler/remover mute **sem mexer no volume** | `IAudioEndpointVolume::GetMute/SetMute` |
| Eventos de dispositivo (conectado, removido, padrão alterado) | `IMMNotificationClient` |
| Evento de mute | `IAudioEndpointVolumeCallback` |
| Mute da sessão do painel no mixer | `IAudioSessionManager2` + `ISimpleAudioVolume` |

`IPolicyConfig` não é documentada oficialmente, mas é a mesma interface que o próprio painel de
som do Windows usa, estável do Windows 7 ao 11, e é usada por ferramentas consolidadas
(SoundSwitch, EarTrumpet, NirCmd). É a **única** forma de trocar o dispositivo padrão sem simular
cliques. Todas as chamadas são protegidas: se falharem, só registram em log.

* O dispositivo é salvo pelo **ID do endpoint** (`{0.0.0.00000000}.{guid}`); o nome amigável é só
  para exibição. Se o driver mudar e o ID sumir, há fallback opcional pelo nome, registrado em log.
* Por padrão o watchdog define o "Multi" como padrão para os papéis Console + Multimídia
  (o que o seletor do Windows faz) e, opcionalmente, Comunicações.
* `SetMute(false)` usa um GUID de contexto próprio, para o watchdog ignorar o eco das próprias
  alterações. O **nível de volume nunca é tocado**.
* Limite anti-briga: se outro programa ficar trocando o padrão em loop, o watchdog corrige no
  máximo N vezes por minuto e registra o conflito.

### Roteamento por aplicativo (requisito 27)
O Windows tem "Volume do aplicativo e preferências de dispositivo", que pode prender um `.exe` a
uma saída. Essa preferência é **por executável**: o Edge do painel e o Edge do funcionário são o
mesmo `msedge.exe`, então forçá-la afetaria o funcionário. Além disso, a API para isso
(`IAudioPolicyConfigFactory`) muda de layout entre builds do Windows e uma chamada errada derruba o
processo (violação de acesso não capturável) — inaceitável para um watchdog de estabilidade.
Decisão: **o watchdog não altera o roteamento por app**; o painel segue o dispositivo padrão
(que o watchdog garante ser o "Multi"). O README explica como verificar se o `msedge.exe` está com
uma preferência fixa e como voltar para "Padrão". O watchdog também desmuta, no mixer, apenas as
sessões de áudio pertencentes à árvore de processos do Edge do painel.

---

## 7. Login automático

1. A senha fica no **Gerenciador de Credenciais do Windows** (`CredWrite`, tipo genérico,
   alvo `GHospPanelWatchdog/GHosp`), protegido por DPAPI e atrelado à conta do Windows.
   Não existe em código, config, logs ou mensagens de erro.
2. Os cookies ficam no perfil dedicado do Edge, então a sessão sobrevive a reinícios do Edge e do PC.
3. A cada verificação, um script mínimo via CDP retorna: URL, `readyState`, se há campo de senha
   visível e o status HTTP da navegação. **Só** se houver formulário de login o watchdog age.
4. Preenchimento: `Runtime.callFunctionOn` com usuário/senha passados como **argumentos** (não
   concatenados em código), atribuindo `value` pelo setter nativo e disparando `input`/`change`
   (compatível com React/Angular/Vue), depois clique **no DOM** do botão (não é clique de mouse).
   Isso ocorre dentro do renderer: nenhum foco do Windows é envolvido.
5. Os seletores são heurísticos (campo de senha visível + campo de texto anterior + botão
   "Entrar/Acessar/submit"), com seletores CSS opcionais nas configurações caso o site mude.
6. Proteção contra bloqueio de conta: no máximo 3 tentativas seguidas (espera 30 s → 2 min →
   10 min); depois, "Login falhou" no status e pausa de 30 min, ou até as credenciais serem salvas
   de novo.
7. Avisos que bloqueiam a tela: o G-HOSP mostra um SweetAlert "Oops…" antes do login. A leitura de
   estado fecha avisos SweetAlert (v1/v2) **sem botão Cancelar** clicando no OK pelo DOM; diálogos
   nativos (`alert/confirm`) são aceitos via `Page.javascriptDialogOpening` → `Page.handleJavaScriptDialog`
   — sem isso, um `alert()` nativo congelaria o JavaScript e pareceria um travamento.
8. Após o login, se o site redirecionar para outra página, o watchdog navega de volta para a URL do painel.

---

## 8. O laço do watchdog (reconciliação)

Um único laço assíncrono, serializado (nunca duas correções simultâneas), acordado por
**eventos** (processo encerrado, monitor alterado, áudio alterado, mute, comando do menu) ou pelo
**timer de fallback** (padrão 5 s). Cada passada compara o estado real com o desejado e corrige
apenas o que divergir:

```
Áudio:   dispositivo configurado presente? é o padrão? está mutado?  → corrige o que for preciso
TV:      monitor configurado presente?  não → minimiza o painel (sem foco) e AGUARDA
Edge:    processo do perfil vivo?       não → lança (respeitando backoff)
Janela:  existe? travada?               não/sim → reabre / mata só o painel e relança
Posição: está na TV e em tela cheia?    não → move (sem foco) + tela cheia
Página:  página de erro? login? outra URL? renderer travado?  → aguarda internet / loga / navega / recarrega
Tudo OK → não faz nada.
```

Cada etapa é isolada por `try/catch`: falha em uma não impede as outras e nunca derruba o app.
O log registra **transições** de estado (ex.: "TV desconectada", "TV reconectada"), não cada passada.

---

## 9. Travamentos

* **Janela do Edge travada**: `IsHungAppWindow` (o mesmo critério do Windows para "Não respondendo").
* **Renderer travado**: `Runtime.evaluate("1")` com timeout de 10 s.
* **Renderer caiu** ("Esta página está com problemas"): evento `Inspector.targetCrashed` → recarrega.
* Só após **3 falhas consecutivas** (≈ 30 s) o watchdog encerra **apenas a árvore do perfil do painel**
  e relança.

---

## 10. Evitar loops de recuperação

* **Nunca** há mais de um lançamento em andamento; após lançar, o watchdog espera a janela
  aparecer (até 30 s) antes de qualquer nova decisão.
* O Chromium já impede dois processos "browser" no mesmo perfil — um segundo `msedge.exe` com o
  mesmo `--user-data-dir` apenas entrega a URL ao existente e sai.
* Backoff exponencial para relançar o Edge: 5 s → 10 s → 20 s → 40 s → 80 s → 160 s → 300 s (teto).
  Zera após 2 minutos de painel saudável.
* **Internet/site fora do ar não reinicia o Edge**: a página de erro é detectada
  (`chrome-error://`, HTTP 5xx) e o watchdog testa a conectividade com uma conexão TCP ao host
  (barata), com backoff 5 s → 10 s → … → 60 s; quando volta, recarrega o painel.
* Login, navegação e correções de áudio têm seus próprios limites (seção 7 e 6).

---

## 11. Baixo consumo

* Nenhum processo auxiliar (sem Playwright/WebDriver/Node).
* Eventos nativos para processo, monitor e áudio; polling de 5 s como rede de segurança, e cada
  passada custa microssegundos quando tudo está OK (checagens de PID, `GetWindowRect`, 1 chamada
  CDP curta, 2–3 chamadas COM de áudio).
* A enumeração de processos `msedge.exe` com leitura de linha de comando só ocorre quando o PID do
  painel é desconhecido.
* Interface (telas de status/configuração) só existe enquanto aberta; fechada, é descartada.
* Log com escrita direta, rotação por tamanho (2 MB × 5 arquivos) e supressão de mensagens repetidas.

---

## 12. Segurança

* Executa como usuário comum (`asInvoker`), sem serviço, sem driver, sem alteração de registro.
* Não altera monitor principal, resolução, rede, impressoras, políticas do Edge ou o perfil do funcionário.
* Credenciais: Gerenciador de Credenciais (DPAPI). Logs nunca recebem conteúdo de página,
  cookies, tokens, payloads CDP ou senha.
* Instância única (mutex por sessão de usuário).

---

## 13. Preparado para "Iniciar com Windows" (não ativado)

* A opção existe nas configurações, **desmarcada e desabilitada** nesta fase.
* O app não depende do diretório de trabalho, tolera iniciar antes da rede/TV/áudio estarem
  prontos (simplesmente aguarda) e é instância única — requisitos para iniciar no logon depois.
* O README descreve como ativar manualmente no futuro (atalho em `shell:startup` ou tarefa
  agendada no logon), sem que o programa modifique o Windows por conta própria.
