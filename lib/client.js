/**
 * dsh-session-hud —— DSH 浏览器半（客户端模块）
 * ============================================================================
 * 只做一件事：在侧边栏底部（设置按钮旁边）放一个按钮，点了让宿主半把 HUD 窗口叫出来。
 *
 * 按 DSH 客户端模块的约定写成「注册一个 lazy factory」：
 *   * 模块 id 必须等于包名 —— 浏览器模块表按这个 id 认人，不重复装 React、不用 CDN；
 *   * React 用 require('react') 从模块表拿；
 *   * factory 返回一个普通 Cordis 插件，用 ctx.slots.inject + ctx.slots.register 占座。
 *
 * 为什么走 HTTP 路由而不是 host.call：`host.call` 那套是「动态客户端半」的受限面
 * （只有 ctx / React / host / styles / console，连 fetch 都没有）。静态客户端模块拿到的
 * 是正常浏览器环境，所以宿主半注册一个 POST 路由、这里 fetch 一下，两端各自都很直白。
 *
 * 样式只用宿主主题 token（--dsw-alias-*），跟随明暗主题；不碰 DOM 结构，
 * 也不去猜其它插件的样式。
 */

window.__ModuleLoader__.load({
  id: 'dsh-session-hud',
  factory(require) {
    const React = require('react')
    const h = React.createElement

    const OPEN_ENDPOINT = '/dsh-session-hud/open'
    const FLASH_MS = 1600

    /** 一个带底座的窗口轮廓，纯 SVG，不依赖图标字体。 */
    function HudGlyph(size) {
      return h('svg', {
        width: size,
        height: size,
        viewBox: '0 0 16 16',
        fill: 'none',
        stroke: 'currentColor',
        strokeWidth: 1.4,
        strokeLinecap: 'round',
        strokeLinejoin: 'round',
        'aria-hidden': true,
        style: { display: 'block' },
      },
        h('rect', { x: 1.7, y: 2.7, width: 12.6, height: 8.6, rx: 1.6 }),
        h('path', { d: 'M8 11.3V14' }),
        h('path', { d: 'M5.4 14h5.2' }),
        h('path', { d: 'M4.4 5.7h4.4' }),
      )
    }

    function OpenHudButton() {
      const [state, setState] = React.useState('idle') // idle | busy | ok | err
      const [hover, setHover] = React.useState(false)

      const open = () => {
        if (state === 'busy') return
        setState('busy')
        fetch(OPEN_ENDPOINT, { method: 'POST', headers: { accept: 'application/json' } })
          .then((response) => {
            if (!response.ok) throw new Error(`HTTP ${response.status}`)
            return response.json().catch(() => ({}))
          })
          .then((body) => {
            setState(body && body.ok ? 'ok' : 'err')
            setTimeout(() => setState('idle'), FLASH_MS)
          })
          .catch(() => {
            setState('err')
            setTimeout(() => setState('idle'), FLASH_MS + 600)
          })
      }

      const color =
        state === 'ok' ? 'var(--dsw-alias-state-success-primary)'
          : state === 'err' ? 'var(--dsw-alias-state-error-primary)'
            : 'var(--dsw-alias-label-secondary)'

      const title =
        state === 'busy' ? '正在打开…'
          : state === 'ok' ? '已请求打开 HUD'
            : state === 'err' ? '打开失败（宿主未响应）'
              : '打开会话 HUD'

      return h('button', {
        type: 'button',
        title,
        'aria-label': '打开会话 HUD',
        onClick: open,
        onMouseEnter: () => setHover(true),
        onMouseLeave: () => setHover(false),
        style: {
          display: 'inline-flex',
          alignItems: 'center',
          justifyContent: 'center',
          width: 28,
          height: 28,
          padding: 0,
          border: 0,
          borderRadius: 6,
          background: hover ? 'var(--dsw-alias-bg-layer-2)' : 'transparent',
          color,
          cursor: state === 'busy' ? 'default' : 'pointer',
          opacity: state === 'busy' ? 0.55 : 1,
          transition: 'background-color .12s ease, color .12s ease',
        },
      }, HudGlyph(16))
    }

    return {
      inject: ['slots'],
      apply(ctx) {
        // inject：槽位可能还没被声明，等它声明了再注册；插件卸载时自动摘掉。
        ctx.slots.inject('sidebar.footer.action', () => ctx.slots.register({
          name: 'sidebar.footer.action',
          id: 'dsh-session-hud-open',
          order: 10,
          label: '打开会话 HUD',
        }, OpenHudButton))
      },
    }
  },
})
