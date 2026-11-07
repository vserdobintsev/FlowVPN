# Сторонние компоненты транспорта Outline

Бинарник собирается из исходников этого репозитория и закреплённых Go-модулей. Код адаптера распространяется под GPL-3.0-only; сторонние компоненты сохраняют собственные лицензии и уведомления. Исходники зависимостей не изменены.

| Компонент | Версия | Лицензия и текст |
| --- | --- | --- |
| [Outline SDK](https://github.com/OutlineFoundation/outline-sdk/tree/v0.0.23) | v0.0.23 | [Apache-2.0](licenses/outline-sdk.txt), Copyright The Outline Authors |
| [go-shadowsocks2](https://github.com/shadowsocks/go-shadowsocks2/tree/v0.1.5) | v0.1.5 | [Apache-2.0](licenses/go-shadowsocks2.txt) |
| [golang.org/x/crypto](https://cs.opensource.google/go/x/crypto/+/2f1f834330b2:) | v0.58.1-0.20261009181718-2f1f834330b2 | [BSD-3-Clause](licenses/go-x-crypto.txt), Copyright 2009 The Go Authors |
| [golang.org/x/sys](https://cs.opensource.google/go/x/sys/+/refs/tags/v0.49.0:) | v0.49.0 | [BSD-3-Clause](licenses/go-x-sys.txt), Copyright 2009 The Go Authors |
| [Go runtime и стандартная библиотека](https://github.com/golang/go/tree/go1.27.3) | go1.27.3 | [BSD-3-Clause](licenses/go.txt), Copyright 2009 The Go Authors |

Точные контрольные суммы модулей находятся в `go.sum`, версия и контрольная сумма toolchain — в `toolchain.lock.json`. `go version -m flowvpn-outline-transport.exe` показывает версии компонентов конкретной сборки. Тестовые зависимости из `go.sum` не означают, что все их пакеты попали в бинарник.

Для `x/crypto` закреплён исправленный upstream-коммит: на момент выбора последний тег
v0.58.0 ещё включал небезопасный OpenPGP-пакет. Выбранная версия устраняет запись
[GO-2026-5932](https://pkg.go.dev/vuln/GO-2026-5932) из зависимостей модуля. FlowVPN не
использует OpenPGP; это обновление зависимостей, а не изменение протокола Outline.
