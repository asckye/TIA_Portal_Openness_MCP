// Run only through the manually dispatched release workflow with its ephemeral GITHUB_TOKEN.
// Never delete assets, move existing tags, or use personal connection credentials.
module.exports = async ({github, context, core}) => {
  const fs = require('node:fs');
  const path = require('node:path');
  const crypto = require('node:crypto');
  const readJson = file => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
  const metadata = readJson('manifest/delivery.json');
  if (!/^\d+\.\d+\.\d+$/.test(metadata.release)) throw new Error('Expected standard release version');
  const tag = `v${metadata.release}`;
  const result = readJson(`bin-build/releases/${tag}/package-result.json`);
  if (result.sourceCommit !== context.sha) throw new Error('Package commit differs from workflow commit');
  const {owner, repo} = context.repo;
  const branch = await github.rest.repos.getBranch({owner, repo, branch: 'master'});
  if (branch.data.commit.sha !== context.sha) throw new Error('Master changed; run the workflow on the latest reviewed commit');
  const archive = result.path;
  const sidecar = archive.replace(/\.zip$/, '.sha256');
  const digest = data => crypto.createHash('sha256').update(data).digest('hex');
  const zipData = fs.readFileSync(archive);
  if (digest(zipData) !== result.sha256 || zipData.length !== result.size) throw new Error('Local ZIP verification failed');
  const releases = await github.paginate(github.rest.repos.listReleases, {owner, repo, per_page: 100});
  let release = releases.find(item => item.tag_name === tag);
  try {
    const ref = await github.rest.git.getRef({owner, repo, ref: `tags/${tag}`});
    // Tags created here are lightweight; a pushed annotated tag is accepted when it points at this
    // commit (dereference the tag object). Never silently repoint any existing tag.
    let target = ref.data.object;
    if (target.type === 'tag') target = (await github.rest.git.getTag({owner, repo, tag_sha: target.sha})).data.object;
    if (target.type !== 'commit' || target.sha !== context.sha) throw new Error('Existing tag points elsewhere; use a new version');
  } catch (error) {
    if (error.status !== 404) throw error;
    await github.rest.git.createRef({owner, repo, ref: `refs/tags/${tag}`, sha: context.sha});
  }
  const notes = fs.readFileSync(`docs/releases/${tag}.md`, 'utf8').replace(/\]\((\.\.\/[^)]+)\)/g,
    (_, target) => `](https://github.com/${owner}/${repo}/blob/${context.sha}/${path.posix.normalize('docs/releases/' + target)})`);
  const body = notes +
    `\n\n完整包：\`${path.basename(archive)}\`（${result.files} 个文件，${result.size} bytes）。` +
    `\n\n源码提交：[${context.sha}](https://github.com/${owner}/${repo}/commit/${context.sha})。` +
    `\n\nZIP SHA256：\n\n\`\`\`text\n${result.sha256}\n\`\`\`\n`;
  if (!release) {
    release = (await github.rest.repos.createRelease({owner, repo, tag_name: tag,
      target_commitish: context.sha, name: tag, body, draft: true, prerelease: false, make_latest: 'false'})).data;
  }
  const assets = await github.paginate(github.rest.repos.listReleaseAssets, {owner, repo, release_id: release.id, per_page: 100});
  for (const filename of [archive, sidecar]) {
    const data = fs.readFileSync(filename);
    const name = path.basename(filename);
    const expected = `sha256:${digest(data)}`;
    let asset = assets.find(item => item.name === name);
    if (!asset) {
      asset = (await github.rest.repos.uploadReleaseAsset({owner, repo, release_id: release.id, name, data,
        headers: {'content-type': filename.endsWith('.zip') ? 'application/zip' : 'text/plain', 'content-length': data.length}})).data;
    }
    if (asset.state !== 'uploaded' || asset.size !== data.length || asset.digest !== expected) {
      throw new Error(`Asset verification failed for ${name}; retained draft/existing release for inspection`);
    }
    core.info(`${name}: ${asset.size} bytes; ${asset.digest}`);
  }
  const published = (await github.rest.repos.updateRelease({owner, repo, release_id: release.id,
    name: tag, body, draft: false, prerelease: false, make_latest: 'true'})).data;
  const archived = [];
  if (context.payload.inputs?.archive_legacy === 'true') {
    for (const old of releases) {
      if (old.id !== release.id && !old.draft && /(?:asckye|defects|readonly)/i.test(old.tag_name)) {
        const updated = (await github.rest.repos.updateRelease({owner, repo, release_id: old.id, draft: true, make_latest: 'false'})).data;
        if (!updated.draft) throw new Error(`Could not archive ${old.tag_name}`);
        archived.push(old.tag_name);
      } else if (!old.draft && /^v\d+\.\d+\.\d+$/.test(old.tag_name) && old.name !== old.tag_name) {
        await github.rest.repos.updateRelease({owner, repo, release_id: old.id, name: old.tag_name, make_latest: 'false'});
      }
    }
  }
  fs.writeFileSync(`bin-build/releases/${tag}/published-release.json`, JSON.stringify({release: published, archived}, null, 2));
  await core.summary.addHeading(tag).addLink('Download complete release', published.html_url)
    .addRaw(`\n\n${result.files} files; SHA256: ${result.sha256}\n\nHistorical releases moved to drafts: ${archived.join(', ') || 'none'}\n`).write();
};
