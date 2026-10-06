# G-HOSP Panel Watchdog

Aplicativo de bandeja do Windows que mantém o **painel de chamada de pacientes do G-HOSP**
funcionando sozinho na TV conectada ao computador, sem atrapalhar o funcionário que usa o monitor principal.

```
EDGE DO PAINEL FUNCIONANDO + PAINEL G-HOSP ABERTO + NA TV + ÁUDIO DA TV SELECIONADO + DESMUTADO
```

Se tudo está certo, ele não faz nada. Se algo sai do lugar, corrige — sem roubar foco, sem mover o
mouse, sem cliques simulados, sem pop-ups.

> A análise técnica completa (por que cada decisão foi tomada) está em [`docs/ARQUITETURA.md`](docs/ARQUITETURA.md).
> O roteiro de testes de aceitação está em [`docs/TESTES.md`](docs/TESTES.md).

---

## 1. Requisitos

* Windows 10 (1809+) ou Windows 11, 64 bits.
* Microsoft Edge instalado (o padrão do Windows).
* **Não** precisa de .NET instalado (o executável é self-contained).
* **Não** precisa de administrador.
* Um segundo monitor/TV (HDMI) para o painel.

---

## 2. Instalação

### Opção A — instalador (recomendado)
Execute `G-HOSP Panel Watchdog Setup.exe`. Ele instala para o usuário atual em
`%LOCALAPPDATA%\Programs\G-HOSP Panel Watchdog`, cria atalhos no Menu Iniciar e, ao final,
oferece **iniciar e abrir as configurações**.

O instalador **não** cria inicialização automática com o Windows.

### Opção B — executável avulso
Copie `G-HOSP Panel Watchdog.exe` para qualquer pasta e execute. Os dados ficam sempre em
`%LOCALAPPDATA%\GHospPanelWatchdog`, independentemente de onde o .exe estiver.

---

## 3. Execução manual

Dê dois cliques em **G-HOSP Panel Watchdog** (Menu Iniciar ou o .exe).

* Não abre janela: aparece só um ícone (uma TV pequena) na bandeja, na área do `^` perto do relógio.
* A cor do ponto no ícone indica o estado: **verde** OK · **azul** trabalhando · **amarelo** aguardando
  (TV ausente, sem internet…) · **vermelho** erro que precisa de atenção (ex.: login recusado) · **cinza** pausado.
* Executar de novo enquanto já está rodando apenas abre a tela de Status (instância única).

Na **primeira execução** a tela de configurações abre automaticamente.

---

## 4. Configuração (menu da bandeja → Configurações)

| Aba | O que configurar |
|---|---|
| **Geral** | URL do painel (padrão `https://painel.g-hosp.com.br/paineis/398`), intervalo da verificação de fallback (padrão 5 s), recarga periódica opcional. |
| **Monitor** | *Automático* (primeiro monitor que não é o principal) ou um monitor específico. |
| **Áudio** | O dispositivo de saída da TV (ex.: **Multi**). |
| **Login** | Usuário e senha do G-HOSP. |
| **Recuperação** | Liga/desliga cada ação automática. |
| **Edge** | Caminho do Edge (automático), limpar o perfil exclusivo do painel. |

### 4.1 Seleção do monitor
* Com 2 monitores, deixe **Automático**.
* Com 3 ou mais, escolha a TV na lista (ou pela bandeja → *Selecionar monitor*).
* O monitor é salvo pelo **caminho do dispositivo** (ex.: `\\?\DISPLAY#SAM0F00#…`), que continua o mesmo
  após desconectar/reconectar o HDMI ou reiniciar o PC. Nada de coordenadas fixas.
* Escolher o monitor **principal** só é permitido com confirmação (útil apenas para testes).

### 4.2 Seleção do áudio
* A lista mostra as saídas de áudio ativas. Escolha a da TV (ex.: **Multi**).
* É salvo o **ID persistente** do dispositivo; o nome é só para exibição.
* Se a TV/driver mudar e o ID deixar de existir, o watchdog tenta achar um dispositivo com o mesmo nome
  (opção ligada por padrão) e registra isso no log. Para atualizar de vez, selecione novamente.
* Por padrão o dispositivo é definido como padrão para *sons/multimídia* e *comunicações*.
* O watchdog **nunca muda o nível de volume** — apenas remove o mute.

### 4.3 Credenciais
* Digite usuário e senha na aba **Login** e clique **Salvar**.
* A senha vai para o **Gerenciador de Credenciais do Windows** (`Painel de Controle → Gerenciador de
  Credenciais → Credenciais do Windows → GHospPanelWatchdog/GHosp`), protegida por DPAPI e vinculada
  ao usuário do Windows. Ela não fica no código, no `config.json`, nos logs nem em mensagens.
* Para trocar a senha, digite a nova e salve. Para manter, deixe o campo em branco.
* Importante: as credenciais pertencem ao **usuário do Windows** que as cadastrou. O watchdog deve
  rodar nesse mesmo usuário.

---

## 5. Como funciona

### Edge exclusivo do painel
O painel roda em um Edge separado, com perfil próprio em
`%LOCALAPPDATA%\GHospPanelWatchdog\EdgeProfile`, aberto em **modo aplicativo** (sem barra de endereço
e sem abas) e em **tela cheia na TV**. O watchdog identifica esse Edge pela linha de comando
(`--user-data-dir` do perfil exclusivo), nunca só por `msedge.exe`. **O Edge do funcionário nunca é
fechado, tocado ou alterado.**

O watchdog conversa com esse Edge por um canal local (Chrome DevTools Protocol em `127.0.0.1`, porta
aleatória) para: saber a URL atual, detectar a tela de login, fazer o login, recarregar, detectar
travamento e colocar em tela cheia. Não há Playwright, WebDriver ou outro processo auxiliar.

### Login automático
Quando o site mostra o formulário de login (`/users/sign_in`), o watchdog preenche usuário e senha
dentro da página e envia. Os cookies ficam no perfil exclusivo, então normalmente o login só é
necessário quando a sessão expira. Se o site recusar o login, ele tenta no máximo 3 vezes
(30 s → 2 min → 30 min) para não bloquear a conta, e o status mostra "Login falhou".

### Watchdog
Um laço único, acordado por **eventos do Windows** (Edge encerrado, monitor alterado, áudio alterado,
mute, retorno de suspensão, desbloqueio) e por uma verificação de fallback a cada 5 s.

| Situação | Ação |
|---|---|
| Tudo OK | Nada. |
| Edge do painel fechado | Reabre na TV, em tela cheia, faz login se precisar. |
| Edge do painel travado (≈30 s sem responder) | Encerra **só** a árvore do Edge do painel e reabre. |
| Página do painel caiu ("Aw, Snap") | Recarrega. |
| TV desconectada | Minimiza o painel **sem foco** (para não cobrir o monitor do funcionário) e aguarda, sem mensagens. |
| TV reconectada | Restaura o painel na TV e em tela cheia. |
| Resolução da TV alterada | Reajusta a tela cheia. |
| Áudio em outro dispositivo | Volta para o dispositivo configurado (Multi). |
| Áudio mutado | Desmuta (volume preservado). |
| Edge do painel mutado no mixer | Desmuta só a sessão do painel. |
| Sem internet / site fora do ar | Aguarda, testando o servidor com espera progressiva (5 s → 60 s), e recarrega quando voltar. **Não** reinicia o Edge. |
| Sessão expirada | Faz login de novo. |
| Alguém abre/arrasta outro programa para a TV | O painel fica sempre à frente na TV e a janela intrusa volta para o monitor principal, **sem tirar o foco** (opção: minimizar ou não fazer nada). Janelas de admin (elevadas) não podem ser movidas, mas ficam atrás do painel. Para usar a TV em manutenção, **pause o watchdog**. |
| Aviso do site (ex.: "Oops…" antes do login) | Clica no OK do aviso pelo DOM (só avisos sem botão Cancelar). Alertas nativos do navegador são aceitos automaticamente. |
| Saiu da página do painel | Volta para a URL do painel após 15 s. |

Reaberturas usam espera progressiva (5 s, 10 s, 20 s, 40 s… até 5 min), zerada após 2 min de painel
saudável. Nunca há dois lançamentos simultâneos, e o próprio Edge impede duas instâncias no mesmo perfil.

---

## 6. Menu da bandeja

| Item | Função |
|---|---|
| **Status** | Tela de diagnóstico (Painel, Edge, Monitor, Áudio, Mute, Internet, Login, Watchdog, última ação). |
| **Abrir painel** | Garante que o painel está aberto (e retoma o watchdog se estiver pausado). |
| **Reiniciar painel** | Fecha e reabre somente o Edge do painel. |
| **Selecionar monitor** | Troca o monitor da TV sem abrir as configurações. |
| **Selecionar áudio** | Troca o dispositivo de áudio do painel. |
| **Testar painel** | Relatório: servidor acessível, Edge, credenciais, monitores, áudio, estado da página. |
| **Pausar watchdog** | Suspende todas as correções (manutenção). |
| **Configurações** | Tela de configuração. |
| **Abrir logs** | Abre a pasta de logs. |
| **Sair** | Pergunta se o painel deve continuar aberto ou ser fechado junto. |

---

## 7. Logs e diagnóstico

* Pasta: `%LOCALAPPDATA%\GHospPanelWatchdog\logs\`
* Arquivo atual: `watchdog.log`; rotação automática em 2 MB, mantendo `watchdog.1.log` … `watchdog.5.log`.
* Registra transições (ex.: "TV desconectada", "Áudio alterado para Multi", "Mute removido",
  "Painel recuperado", "Login recuperado"), não cada verificação. Mensagens repetidas são suprimidas por 10 min.
* Nunca registra senha, cookies, tokens ou conteúdo da página.

**Relatório de diagnóstico** (somente leitura, não altera nada):
Menu Iniciar → *Diagnóstico do painel*, ou `"G-HOSP Panel Watchdog.exe" --diag`.
Gera `logs\diagnostico.txt` com monitores (e seus caminhos), saídas de áudio (e IDs), Edge e estado.

### Linha de comando
| Argumento | Efeito |
|---|---|
| *(nenhum)* | Inicia o watchdog (ou mostra o Status se já estiver rodando). |
| `--config` | Abre as configurações. |
| `--status` | Mostra o Status da instância em execução. |
| `--diag` | Gera o relatório de diagnóstico. |
| `--stop` | Encerra o watchdog; o painel continua aberto. |
| `--stop-all` | Encerra o watchdog e fecha o painel. |
| `--purge-data` | Encerra tudo e apaga configuração, credenciais, perfil do painel e logs. |

---

## 8. Como reiniciar o painel

* Bandeja → **Reiniciar painel**, ou
* Bandeja → **Status** → **Reiniciar painel**, ou
* Feche a janela do painel (Alt+F4 com ela em foco): o watchdog a reabre sozinho.

Se o login ficar preso (ex.: senha trocada no G-HOSP): atualize a senha em Configurações → Login.
Se a sessão ficar inconsistente: Configurações → Edge → **Limpar perfil do painel**.

---

## 9. Pontos de atenção

* **Roteamento de áudio por aplicativo**: o Windows permite fixar uma saída por programa
  (Configurações → Sistema → Som → Mixer de volume). Essa preferência vale para o `msedge.exe` inteiro
  (painel **e** Edge do funcionário), por isso o watchdog **não** a altera. Verifique que o Microsoft Edge
  está em **Padrão** nesse mixer — assim o painel segue o dispositivo padrão, que o watchdog garante ser o Multi.
* **Política do Edge**: se a TI bloquear `RemoteDebuggingAllowed` por GPO, o watchdog entra em
  *modo limitado* (mantém o painel aberto e na TV, mas sem login automático e sem detecção de página).
  O log informa isso.
* **Tela bloqueada / proteção de tela**: quando o Windows bloqueia a sessão, a tela de bloqueio aparece
  em todos os monitores, inclusive na TV. Isso é do Windows. A opção *Impedir que o Windows desligue o
  vídeo* (Geral) evita o desligamento por inatividade, mas fica desligada por padrão porque afeta
  todos os monitores.
* **Canal de controle local**: a porta de depuração do Edge do painel aceita apenas conexões de
  `127.0.0.1`, em porta aleatória. Um programa malicioso já rodando como o mesmo usuário do Windows
  poderia usá-la, mas esse programa também teria acesso aos cookies do perfil no disco.

---

## 10. Desinstalação

Configurações do Windows → Aplicativos → **G-HOSP Panel Watchdog** → Desinstalar
(ou Menu Iniciar → *Desinstalar G-HOSP Panel Watchdog*).

O desinstalador:
1. encerra o watchdog e fecha o painel;
2. remove o programa e os atalhos;
3. **pergunta** se deve apagar também configuração, credenciais, perfil do Edge do painel e logs.
   O padrão é **manter** (úteis para diagnóstico ou reinstalação).

Como não há inicialização automática nesta versão, não há entrada de inicialização para remover.

Remoção manual completa: `"G-HOSP Panel Watchdog.exe" --purge-data` e depois apague o .exe.

---

## 11. Futuro: iniciar com o Windows (NÃO fazer durante os testes)

Esta versão **não** configura inicialização automática, e a opção "Iniciar com o Windows" aparece
desativada. Depois que todos os testes de [`docs/TESTES.md`](docs/TESTES.md) forem aprovados, escolha **uma** das opções:

**A) Pasta Inicializar (mais simples)**
1. `Win + R` → `shell:startup` → Enter.
2. Crie ali um atalho para `G-HOSP Panel Watchdog.exe`.

**B) Agendador de Tarefas (permite atraso após o logon)**
1. Agendador de Tarefas → Criar Tarefa.
2. Geral: "Executar somente quando o usuário estiver conectado"; **não** marque "privilégios mais altos".
3. Disparadores: "Ao fazer logon" do usuário do painel; atraso de 30 s (opcional).
4. Ações: iniciar `G-HOSP Panel Watchdog.exe`.
5. Configurações: desmarque "Parar a tarefa se ela for executada por mais de…".

O programa já está preparado para isso: instância única, caminhos absolutos, e tolera iniciar antes da
rede, da TV ou do áudio estarem prontos (simplesmente aguarda).

---

## 12. Build (desenvolvedor)

Requisitos: .NET SDK 10 e, para o instalador, Inno Setup 6.

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Saída em `dist\`: `G-HOSP Panel Watchdog.exe` (self-contained, arquivo único) e
`G-HOSP Panel Watchdog Setup.exe`.

Estrutura:

```
src/GHospPanelWatchdog/
  Program.cs                 entrada, linha de comando, instância única
  Watchdog/WatchdogEngine.cs laço de reconciliação, backoff, status
  Edge/PanelBrowser.cs       Edge exclusivo: processo, janela, login, página
  Edge/CdpClient.cs          cliente DevTools mínimo (WebSocket local)
  Display/MonitorService.cs  monitores (EnumDisplayMonitors + QueryDisplayConfig)
  Display/WindowPlacer.cs    posicionamento sem foco
  Audio/AudioService.cs      dispositivo padrão, mute, sessões (Core Audio + IPolicyConfig)
  Security/CredentialStore.cs Gerenciador de Credenciais do Windows
  Logging/Log.cs             log com rotação
  UI/                        bandeja, status, configurações
installer/                   script do Inno Setup
docs/                        arquitetura e testes
```

Variável de ambiente para testes isolados: `GHOSP_WATCHDOG_DATA_DIR=<pasta>` faz o programa usar outra
pasta de dados (configuração, perfil, logs) em vez de `%LOCALAPPDATA%\GHospPanelWatchdog`.
