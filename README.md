# YAHCW — Yet Another Hot Corners for Windows

**YAHCW** é um utilitário leve para Windows que combina a navegação por cantos de tela (*hot corners*) com uma visão geral unificada de janelas ativas, ajuste central de brilho dos monitores e seleção rápida de dispositivos de saída de áudio.

---

## Recursos Principais

- **Visão Geral Unificada:** Uma interface única em tela cheia exibe miniaturas dinâmicas das janelas abertas (via Desktop Window Manager - DWM), integrada na mesma tela com o controle de brilho e as saídas de áudio.
- **Dois Cantos de Tela Ativos:** Os cantos superior esquerdo (*Top-Left*, padrão) e inferior direito (*Bottom-Right*) acionam a visão geral. Ambos compartilham as mesmas proteções, com rearmamento controlado e sem acúmulo indesejado de tempo de permanência (*dwell*).
- **Atalho Global de Teclado:** Suporte a acionamento rápido via teclado (padrão: `Ctrl + Alt + Espaço`).
- **Ajuste de Brilho Integrado:** Comunicação local via canal seguro (*Named Pipe* restrito ao token do usuário) com o utilitário auxiliar **BrightTray**, permitindo alterar o brilho do monitor diretamente pela tela geral.
- **Troca Rápida de Saída de Áudio:** Painéis dedicados para alternar a saída de reprodução padrão do Windows (papéis de console e multimídia) em um clique, com restauração automática em caso de falha.
- **Filtro de Busca e Navegação:** Pesquisa dinâmica por título de janela ou nome de processo com navegação por teclado (`Setas`, `Page Up/Down`, `Enter` para ativar, `Esc` para fechar).
- **Guardas de Segurança (Fail-Closed):** Bloqueio durante confinamento detectável do cursor, botões do mouse pressionados e área de trabalho insegura (UAC/Winlogon). Aplicativos em tela cheia são permitidos; tela cheia não é tratada como sinônimo de jogo.

---

## Requisitos do Sistema

- **Sistema Operacional:** Windows 10 ou Windows 11 (64-bit).
- **Ambiente de Execução / Compilação:** **.NET Framework 4.5+** (nativo do Windows; utiliza o compilador `csc.exe` disponível em `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`).
- **Sem pacotes NuGet:** Depende do .NET Framework e dos componentes desktop do Windows. O brilho exige o BrightTray incluído neste repositório e suporte do monitor a DDC/CI ou WMI.

---

## Como Compilar

Abra o PowerShell no diretório raiz do projeto:

### 1. Compilar o YAHCW
```powershell
# Compila o executável principal e executa os auto-testes deterministas
powershell -ExecutionPolicy Bypass -File .\build.ps1 -OutFile "YAHCW.exe" -RunTests
```

### 2. Compilar o BrightTray (Assistente de Brilho)
O código-fonte do companheiro de brilho está localizado em `integrations/brighttray`:
```powershell
powershell -ExecutionPolicy Bypass -File .\integrations\brighttray\build.ps1 -OutFile "BrightTray.exe"
```

Depois de compilar, inicie o companheiro e o aplicativo:

```powershell
Start-Process .\integrations\brighttray\BrightTray.exe
Start-Process .\YAHCW.exe
```

O YAHCW continua disponível sem BrightTray; nesse caso, o controle de brilho informa a indisponibilidade. DDC/CI pode precisar ser habilitado no menu físico do monitor.

---

## Execução e Linha de Comando

```text
Uso:
  YAHCW.exe                      Inicia o utilitário na bandeja do sistema (modo padrão)
  YAHCW.exe --overview           Abre a visão geral (valida guardas antes da exibição)
  YAHCW.exe --diagnose           Exibe relatório diagnóstico do ambiente e guardas
  YAHCW.exe --controls-diagnose  Inspeciona áudio e comunicação com o BrightTray
  YAHCW.exe --self-test          Executa a bateria com 23 auto-testes deterministas
  YAHCW.exe --help               Exibe opções de linha de comando
```

---

## Configuração (`yahcw.config.json`)

As configurações podem ser ajustadas no arquivo `yahcw.config.json` no mesmo diretório do executável:

```json
{
  "corner": "TopLeft",
  "dwellMs": 180,
  "triggerSizePx": 2,
  "rearmDistancePx": 24,
  "cooldownMs": 700,
  "hotkeyEnabled": true,
  "hotkeyModifiers": "Ctrl+Alt",
  "hotkeyKey": "Space",
  "maxThumbnails": 32,
  "suspendOnGameProcessRunning": false,
  "checkRunningGamesIntervalMs": 5000,
  "isPaused": false,
  "blockedProcesses": []
}
```

- `corner`: Canto primário de ativação (`TopLeft`, `TopRight`, `BottomLeft`, `BottomRight`). O canto inferior direito permanece disponível como gatilho secundário unificado.
- `dwellMs`: Tempo mínimo (em milissegundos) que o cursor deve permanecer no canto para acionar.
- `triggerSizePx`: Tolerância em pixels a partir do vértice do monitor para considerar o cursor no canto.
- `rearmDistancePx`: Distância mínima que o cursor deve percorrer para armar novamente o gatilho.
- `cooldownMs`: Período de proteção contra reativações acidentais consecutivas.
- `blockedProcesses`: Lista de processos que bloqueiam a ativação quando em primeiro plano.
- `suspendOnGameProcessRunning`: Se ativado, também bloqueia quando um processo listado está em segundo plano. Desativado por padrão.

Após editar o JSON, saia pela bandeja e abra novamente. O atalho desta versão é fixo em `Ctrl+Alt+Espaço`; os campos de tecla/modificadores são reservados. O limite de prévias por página também é ajustado pela interface para preservar a legibilidade.

---

## Limitações Técnicas e Avisos de Compatibilidade

1. **API de Áudio Não Documentada (`IPolicyConfig`):**
   A troca dinâmica da saída padrão de reprodução utiliza a interface COM não documentada `IPolicyConfig`. A enumeração usa `IMMDeviceEnumerator`, uma API documentada. A interface de troca não possui garantia formal da Microsoft e seu comportamento pode sofrer alterações entre versões ou atualizações do sistema operacional. Aplicativos com saídas de áudio fixadas manualmente em suas próprias configurações podem ignorar a alteração do dispositivo padrão.

2. **Tela Cheia Exclusiva e Anti-Cheat:**
   O YAHCW opera na camada de composição do Windows (DWM). **Não há garantia de sobreposição em jogos rodando em modo de tela cheia exclusiva (*Exclusive Fullscreen*)** ou em títulos protegidos por sistemas anti-cheat em nível de kernel, que podem impedir que a visão geral apareça acima do conteúdo. O aplicativo não instala hooks globais de mouse/teclado, não injeta entrada e não oferece garantia de compatibilidade com anti-cheats.

3. **Limitações das Guardas de Tela:**
   `GetClipCursor` identifica confinamento menor que a tela virtual. Um jogo que centraliza o mouse sem usar essa API, ou o confina à tela virtual inteira, pode não ser identificado. Use a lista manual ou pause os cantos nesses casos. Estado de tela cheia e geometria são informativos, não bloqueios automáticos.

4. **Foco e prévias:**
   O Windows pode negar foco automático; a visão geral permanece clicável para aquisição de foco. Janelas minimizadas ou conteúdo protegido podem exibir ícone/título em vez de prévia ao vivo. Não há promessa de consumo zero: existem timers, memória do runtime e trabalho do compositor/GPU.

---

## Testes e Qualidade

O projeto acompanha suites completas de validação:

- **Auto-testes:** Execute `.\YAHCW.exe --self-test` para rodar 23 testes deterministas de lógica, detecção geométrica, resiliência a configurações corrompidas e guardas de segurança.
- **Testes de Integração:** Execute `.\tests\run.ps1` para validar a interface visual, ciclo de foco, gerenciamento de handles GDI/USER e promoções nativas de ordem Z. Os testes usam alvos simulados e **não realizam gravações no hardware nem alteram as saídas reais de som**.

Mais detalhes sobre a arquitetura de testes podem ser encontrados em [`docs/test-notes.md`](docs/test-notes.md).
