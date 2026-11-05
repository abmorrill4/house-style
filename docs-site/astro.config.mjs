// The House Style documentation site, published at https://gethousestyle.com/docs/ next to the static site in site/.
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';

export default defineConfig({
  site: 'https://gethousestyle.com',
  base: '/docs',
  trailingSlash: 'always',
  build: { format: 'directory' },
  integrations: [
    starlight({
      title: 'House Style',
      description: 'Guides for writers and style guide owners who use House Style, the style checker for MadCap Flare.',
      logo: { light: './src/assets/mark.svg', dark: './src/assets/mark-white.svg', alt: '' },
      favicon: '/favicon.svg',
      head: [
        { tag: 'link', attrs: { rel: 'icon', href: '/docs/favicon.ico', sizes: '48x48' } },
        { tag: 'link', attrs: { rel: 'apple-touch-icon', href: '/docs/apple-touch-icon.png' } },
        { tag: 'meta', attrs: { property: 'og:image', content: 'https://gethousestyle.com/og.png' } },
        { tag: 'meta', attrs: { name: 'twitter:image', content: 'https://gethousestyle.com/og.png' } },
      ],
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/abmorrill4/house-style' }],
      editLink: { baseUrl: 'https://github.com/abmorrill4/house-style/edit/main/docs-site/' },
      lastUpdated: false,
      customCss: ['./src/styles/custom.css'],
      components: {
        Sidebar: './src/components/Sidebar.astro',
        Footer: './src/components/Footer.astro',
      },
      sidebar: [
        { label: 'Get started', items: [{ autogenerate: { directory: 'get-started' } }] },
        { label: 'Check your writing', items: [{ autogenerate: { directory: 'check-your-writing' } }] },
        { label: 'AI assists', items: [{ autogenerate: { directory: 'ai-assists' } }] },
        { label: 'Team style guide', items: [{ autogenerate: { directory: 'team-style-guide' } }] },
        { label: 'Settings and privacy', items: [{ autogenerate: { directory: 'settings-and-privacy' } }] },
        { label: 'Troubleshoot', slug: 'troubleshooting' },
        {
          label: 'Reference',
          items: [
            { label: 'Rule reference', slug: 'reference/rules' },
            { label: 'Release notes', slug: 'release-notes' },
          ],
        },
      ],
      plugins: [
        // Fails the build on a broken internal link or heading link. The pages of the main site (/privacy.html and so on)
        // live outside this build.
        starlightLinksValidator({
          errorOnRelativeLinks: false,
          exclude: ['/privacy.html', '/install.html', '/guide/**', '/'],
        }),
      ],
    }),
  ],
});
