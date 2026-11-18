"""在固定 PSD 坐标比较真实字面和外描边；不自动对齐，不把诊断量称为还原百分比。"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from compare_psd_fill_probe import FOLDER, mask_iou, statistics


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def read_image(path):
    result = np.asarray(Image.open(path).convert('RGBA'), dtype=np.float64) / 255
    if result.shape != (2400, 1080, 4):
        raise ValueError('探针画布不符，不允许缩放拟合')
    return result


def metrics(ref, actual, rect):
    x, y, right, bottom = rect
    a, b = ref[y:bottom, x:right], actual[y:bottom, x:right]
    return dict(reference=statistics(a, (x, y)), actual=statistics(b, (x, y)),
                absoluteDifference=float(np.abs(a-b).sum()),
                iou50=mask_iou(a, b, .5))


def compare(id, folder=FOLDER):
    inventory_path = folder.parent / 'inventory.json'
    inventory = load_json(inventory_path)
    job = next(j for j in inventory['jobs'] if j['id'] == id)
    if job['fonts'] != ['TOXIA'] or [e['key'] for e in job['activeEffects']] != ['FrFX']:
        raise ValueError('此比较只支持单色 TOXIA、外描边且没有其他启用效果的真实样本')
    ps_path = folder / f'effects-ps-{id}-cached.png'
    ps_meta = load_json(ps_path.with_suffix('.json'))
    if sha(ps_path) != ps_meta['pngSha256'] or ps_meta['mode'] != 'cached-effects-from-disk-copy':
        raise ValueError('Photoshop 证据身份不符')
    if any(sha(Path(p)) != digest for p, digest in ps_meta['sourceHashes'].items()):
        raise ValueError('源 PSD 或 meta 已变化，停止复用旧证据')
    if sha(Path(inventory['source'])) != inventory['sha256']:
        raise ValueError('类型/颜色清单不是当前 PSD，不能分离覆盖率')
    ps = read_image(ps_path)
    # 两种单色的预乘合成用于估算字面覆盖；Alpha 是包含描边透明度的直接证据。
    face_color = np.array(job['color'][:3])
    outline_color = np.array(job['strokeColor'][:3])
    difference_color = face_color-outline_color
    if np.dot(difference_color,difference_color) < .01:
        raise ValueError('字面和描边颜色太接近，不能可靠分离字面覆盖')
    ps_outer = ps[:, :, 3]
    ps_face = np.clip(np.sum((ps[:, :, :3]-outline_color) * ps_outer[:, :, None] * difference_color, axis=2) / np.dot(difference_color, difference_color), 0, 1)
    background = 31 / 255
    ps_composite = ps[:, :, :3] * ps_outer[:, :, None] + background * (1 - ps_outer[:, :, None])
    x, y, width, height = job['rasterRect']
    rect = (max(0, x-12), max(0, y-12), min(1080, x+width+12), min(2400, y+height+12))
    report = dict(layerId=id, text=job['text'], fontSize=job['fontSizeUi'], strokePixels=job['strokePixels'], rect=rect,
                  source=ps_meta, sourceFaceCoverage='由原图预乘RGB和填充/描边单色估算；PSD Alpha直接作为有效外轮廓覆盖',
                  scope='原位、参考分辨率；单个真实字体，不推导所有字体或运行时缩放通式', comparisons={})
    visuals = {'Photoshop 原稿缓存＋原描边': ps_composite}
    variants = [('baseline', '当前正式参数'), ('direction', '方向性原型＋相同描边'),
                ('balanced', '左右分配 25:75＋相同描边'), ('symmetric', '左右分配 50:50＋相同描边')]
    for name, label in variants:
        path = folder / f'effects-tmp-{id}-{name}.png'
        if name in ('balanced','symmetric') and not path.exists():
            continue
        metadata = load_json(path.with_suffix('.json'))
        mask_path = folder / f'effects-tmp-{id}-{name}-mask.png'
        mask_metadata = load_json(mask_path.with_suffix('.json'))
        for data in [metadata, mask_metadata]:
            if not data['effectsProbe'] or not data['layoutPreserved'] or not data['outlineEnabled'] or data['normalStyle']:
                raise ValueError('效果探针没有固定粗体排版及原始位置')
            if data['after']['fontStyle'] != 'Bold' or data['before']['fontSize'] != data['after']['fontSize']:
                raise ValueError('字重或字号意外变化')
            for key in ['characterSpacing','wordSpacing','lineSpacing','paragraphSpacing','characterHorizontalScale','fontFeatures','anchoredPosition']:
                if data['before'][key] != data['after'][key]:
                    raise ValueError('效果探针改变排版参数：' + key)
            original = json.loads(data['sourceMaterialJson'])['Material']['m_SavedProperties']
            current = json.loads(data['probeMaterialJson'])['Material']['m_SavedProperties']
            of = {p['first']: p['second'] for p in original['m_Floats']}
            cf = {p['first']: p['second'] for p in current['m_Floats']}
            oc = {p['first']: p['second'] for p in original['m_Colors']}
            cc = {p['first']: p['second'] for p in current['m_Colors']}
            if of['_OutlineWidth'] != cf['_OutlineWidth'] or of['_OutlineSoftness'] != cf['_OutlineSoftness']:
                raise ValueError('效果探针意外改变了描边宽度或软化')
            for key in ['_FaceColor','_OutlineColor']:
                if oc[key]['a'] != cc[key]['a']:
                    raise ValueError('覆盖图不得忽略原始透明度')
        if metadata['maskColors'] or not mask_metadata['maskColors'] or metadata['after'] != mask_metadata['after']:
            raise ValueError('颜色图与覆盖图不是相同布局')
        mask = read_image(mask_path)
        if np.max(mask[:, :, 2]) > 0:
            raise ValueError('红绿覆盖图中存在非预期蓝色')
        face = mask[:, :, 0]
        outer = np.clip(mask[:, :, 0] + mask[:, :, 1], 0, 1)
        rgb = read_image(path)[:, :, :3]
        xx, yy, rr, bb = rect
        report['comparisons'][name] = dict(
            face=metrics(ps_face, face, rect), outer=metrics(ps_outer, outer, rect),
            colorL1=float(np.abs(ps_composite[yy:bb, xx:rr] - rgb[yy:bb, xx:rr]).sum()),
            layoutPreserved=True, fontSize=metadata['after']['fontSize'],
            characterSpacing=metadata['after']['characterSpacing'], directionalBoldX=metadata['directionalBoldX'],
            directionalBias=metadata.get('directionalBias',1),
            colorPngSha256=sha(path), maskPngSha256=sha(mask_path))
        visuals[label] = rgb
    return report, visuals


def panel(visuals, rect, output, title, scale=2):
    font_path = 'C:/Windows/Fonts/msyh.ttc'
    font = ImageFont.truetype(font_path, 17)
    x, y, right, bottom = rect
    row_height = (bottom-y)*scale + 38
    canvas = Image.new('RGB', (max(700, (right-x)*scale+24), 62+len(visuals)*row_height), (24,24,24))
    draw = ImageDraw.Draw(canvas)
    draw.text((12,10), title, fill='white', font=font)
    draw.text((12,34), f'同背景、同位置、{scale}×等比；字距/字号/位置均未调整', fill='#bbbbbb', font=font)
    for i,(label,rgb) in enumerate(visuals.items()):
        top=62+i*row_height
        draw.text((12,top),label,fill='white',font=font)
        tile=Image.fromarray(np.rint(rgb[y:bottom,x:right]*255).astype(np.uint8))
        tile=tile.resize((tile.width*scale,tile.height*scale),Image.Resampling.NEAREST)
        canvas.paste(tile,(12,top+28))
    canvas.save(output)


def main(ids):
    reports=[]
    for id in ids:
        report,visuals=compare(id)
        prefix=FOLDER/f'effects-comparison-{id}'
        prefix.with_suffix('.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        panel(visuals,report['rect'],prefix.with_suffix('.png'),f"ID {id} · {report['text'].strip()} · {report['fontSize']:.1f}px / 外描边 {report['strokePixels']:g}px")
        reports.append(report)
        print(json.dumps(dict(id=id, **{mode:{k:data[k]['absoluteDifference'] for k in ['face','outer']} for mode,data in report['comparisons'].items()}),ensure_ascii=False))
    (FOLDER/'effects-summary.json').write_text(json.dumps(reports,ensure_ascii=False,indent=2),encoding='utf-8')
    if 1057 in ids and 1058 in ids:
        # 两段仍在原画布坐标中，使用不相交的固定窗口合成，不移动文字。
        rows={}
        for id,rect in [(1057,(315,88,635,155)),(1058,(635,88,775,155))]:
            _,visuals=compare(id)
            for label,rgb in visuals.items():
                if label == '左右分配 25:75＋相同描边': continue
                if label not in rows: rows[label]=np.full((2400,1080,3),31/255,dtype=np.float64)
                x,y,right,bottom=rect
                rows[label][y:bottom,x:right]=rgb[y:bottom,x:right]
        panel(rows,(315,88,775,155),FOLDER/'effects-title-review.png','原稿外描边对照 · 仅已匹配 TOXIA 的两个标题层',2)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('ids',type=int,nargs='+')
    main(parser.parse_args().ids)
